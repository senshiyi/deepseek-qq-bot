using System.Runtime.CompilerServices;
using System.Text.Json;
using AudioBridge;

public static class CompanionChecks
{
    private static readonly DateTimeOffset InitialNow = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("empty store reports no mood or diary and creates no files", Defaults),
            ("three diary levels expire after a week, a month or never", Levels),
            ("expired entries are forgotten but kept entries survive", Forgotten),
            ("mood, wakeup counters and audit marks persist per contact", State),
            ("wake gate honours idle, cooldown, daily limit and busy states", WakeGate),
            ("journal drafts are parsed strictly and reject unknown fields", Journal),
            ("invalid entries, oversized values and contact caps are rejected", Bounds),
            ("only the trigger contact's state reaches a private reply context", Isolation),
            ("audit boundary follows the configured local hour", Boundary),
            ("wake loop sends once, respects cooldown and the daily limit", WakeLoop),
            ("wake loop stays quiet when paused, busy or the model declines", WakeLoopQuiet),
            ("daily audit writes the diary once and updates the mood", DailyAudit)
        };
        foreach (var check in checks)
        {
            await check.Run().WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine("PASS companion: " + check.Name);
        }
        return checks.Length;
    }

    private static Task Defaults()
    {
        using var folder = new Folder();
        var store = folder.Store();
        var snapshot = store.Get(123);
        Check(snapshot.Mood == "" && snapshot.Entries.Count == 0 && snapshot.LastAuditAt is null, "empty store fabricated state");
        Check(store.BuildContext(123).Contains("当前心情", StringComparison.Ordinal), "empty context missed the mood line");
        Check(store.PurgeExpired() == 0 && !System.IO.Directory.Exists(folder.Directory), "read-only defaults created storage");
        Check(store.LoadSettings() == new CompanionSettings { Audit = true }, "default settings were not conservative");
        return Task.CompletedTask;
    }

    private static Task Levels()
    {
        using var folder = new Folder();
        var store = folder.Store();
        var normal = store.AddEntry(123, DiaryLevels.Normal, "他说今天很累", "心疼", "想抱抱他");
        var important = store.AddEntry(123, DiaryLevels.Important, "他通过了面试", "高兴", "");
        var vital = store.AddEntry(123, DiaryLevels.Vital, "他说会一直在", "安心", "也想一直陪着你");
        Check(normal.ExpiresAt == folder.Now.AddDays(7), "normal diary did not expire after a week");
        Check(important.ExpiresAt == folder.Now.AddDays(30), "important diary did not expire after a month");
        Check(vital.ExpiresAt is null, "vital diary was given an expiry");
        folder.Now = folder.Now.AddDays(8);
        var snapshot = store.Get(123);
        Check(snapshot.Entries.Count == 2 && snapshot.Entries.All(item => item.Level != DiaryLevels.Normal), "normal diary survived its week");
        folder.Now = folder.Now.AddDays(30);
        Check(store.Get(123).Entries.Single().Level == DiaryLevels.Vital, "important diary survived its month");
        return Task.CompletedTask;
    }

    private static Task Forgotten()
    {
        using var folder = new Folder();
        var store = folder.Store();
        store.AddEntry(123, DiaryLevels.Normal, "一周前的普通事", "", "");
        store.AddEntry(456, DiaryLevels.Vital, "永远记得", "", "");
        System.IO.Directory.CreateDirectory(folder.Directory);
        File.WriteAllText(folder.Doc(789), "corrupt-companion-file-must-remain");
        var damaged = File.ReadAllBytes(folder.Doc(789));
        folder.Now = folder.Now.AddDays(8);
        Check(store.PurgeExpired() == 1, "forgetting pass did not report exactly the expired entry");
        Check(store.Get(123).Entries.Count == 0 && store.Get(456).Entries.Count == 1, "forgetting removed the wrong entries");
        Check(File.ReadAllBytes(folder.Doc(789)).SequenceEqual(damaged), "forgetting pass reset a damaged file");
        Check(store.PurgeExpired() == 0, "repeat forgetting pass found work");
        return Task.CompletedTask;
    }

    private static Task State()
    {
        using var folder = new Folder();
        var store = folder.Store();
        store.SetMood(123, "有点闷闷的");
        store.MarkWoken(123);
        store.MarkWoken(123);
        store.MarkAudited(123, folder.Now);
        var snapshot = store.Get(123);
        Check(snapshot.Mood == "有点闷闷的" && snapshot.MoodUpdatedAt == folder.Now, "mood was not persisted");
        Check(snapshot.WakeupsToday == 2 && snapshot.LastWakeupAt == folder.Now, "wakeup counter missed today");
        Check(snapshot.LastAuditAt == folder.Now, "audit mark was not persisted");
        folder.Now = folder.Now.AddDays(1);
        store.MarkWoken(123);
        Check(store.Get(123).WakeupsToday == 1, "wakeup counter did not reset the next day");
        var other = store.Get(456);
        Check(other.Mood == "" && other.WakeupsToday == 0, "one contact's state leaked into another");
        store.SetMood(123, "");
        Check(store.Get(123).MoodUpdatedAt is null, "clearing the mood kept a stale timestamp");
        return Task.CompletedTask;
    }

    private static Task WakeGate()
    {
        var settings = new CompanionSettings { Enabled = true, IdleMinutes = 120, CooldownMinutes = 120, DailyLimit = 3 };
        var now = InitialNow;
        var idleTwoHours = now.AddMinutes(-121);
        Check(!CompanionStore.ShouldWake(settings with { Enabled = false }, CompanionSnapshot.Empty(1), idleTwoHours, now, false, 0), "disabled wake gate opened");
        Check(!CompanionStore.ShouldWake(settings, CompanionSnapshot.Empty(1), null, now, false, 0), "wake gate opened without any earlier message");
        Check(!CompanionStore.ShouldWake(settings, CompanionSnapshot.Empty(1), now.AddMinutes(-119), now, false, 0), "wake gate opened before the idle threshold");
        Check(CompanionStore.ShouldWake(settings, CompanionSnapshot.Empty(1), idleTwoHours, now, false, 0), "wake gate stayed closed after the idle threshold");
        Check(!CompanionStore.ShouldWake(settings, CompanionSnapshot.Empty(1), idleTwoHours, now, true, 0), "wake gate opened while the responder is busy");
        Check(!CompanionStore.ShouldWake(settings, CompanionSnapshot.Empty(1), idleTwoHours, now, false, 1), "wake gate opened with pending messages");
        var woken = CompanionSnapshot.Empty(1) with { LastWakeupAt = now.AddMinutes(-30) };
        Check(!CompanionStore.ShouldWake(settings, woken, idleTwoHours, now, false, 0), "wake gate ignored the cooldown");
        var capped = CompanionSnapshot.Empty(1) with { WakeupsToday = 3 };
        Check(!CompanionStore.ShouldWake(settings, capped, idleTwoHours, now, false, 0), "wake gate ignored the daily limit");
        Check(!CompanionStore.ShouldWake(settings with { DailyLimit = 0 }, CompanionSnapshot.Empty(1), idleTwoHours, now, false, 0), "zero daily limit still woke");
        Check(CompanionStore.ShouldWake(settings, CompanionSnapshot.Empty(1) with { WakeupsToday = 2 }, idleTwoHours, now, false, 0), "below-limit wake was refused");
        return Task.CompletedTask;
    }

    private static Task Journal()
    {
        var draft = CompanionJournal.Parse("{\"mood\":\"有点想他\",\"entries\":[{\"level\":\"important\",\"text\":\"他今天面试\",\"moodAtTime\":\"担心\",\"unsaid\":\"希望顺利\"},{\"level\":\"normal\",\"text\":\"\",\"moodAtTime\":\"\",\"unsaid\":\"\"}]}");
        Check(draft.Mood == "有点想他" && draft.Entries.Count == 1, "journal draft dropped or kept the wrong entries");
        Check(draft.Entries[0] == new DiaryDraft(DiaryLevels.Important, "他今天面试", "担心", "希望顺利"), "journal draft fields changed");
        Check(CompanionJournal.Parse("{\"mood\":\"平静\",\"entries\":[]}").Entries.Count == 0, "empty journal draft was rejected");
        Reject("{\"mood\":\"平静\",\"entries\":[{\"level\":\"wrong\",\"text\":\"x\",\"moodAtTime\":\"\",\"unsaid\":\"\"}]}");
        Reject("{\"mood\":\"平静\",\"entries\":[{\"level\":\"normal\",\"text\":\"x\",\"moodAtTime\":\"\",\"unsaid\":\"\",\"extra\":\"y\"}]}");
        Reject("{\"mood\":\"平静\",\"entries\":[],\"extra\":\"y\"}");
        Reject("{\"entries\":[]}");
        Reject("[]");
        Reject("not json");
        return Task.CompletedTask;
    }

    private static Task Bounds()
    {
        using var folder = new Folder();
        var store = folder.Store();
        Reject(() => store.AddEntry(123, "wrong", "x", "", ""));
        Reject(() => store.AddEntry(123, DiaryLevels.Normal, "  ", "", ""));
        Reject(() => store.AddEntry(123, DiaryLevels.Normal, new string('x', CompanionStore.MaximumTextCharacters + 1), "", ""));
        Reject(() => store.AddEntry(0, DiaryLevels.Normal, "x", "", ""));
        Reject(() => store.AddEntry(123, DiaryLevels.Normal, "x", new string('m', CompanionStore.MaximumMoodCharacters + 1), ""));
        Reject(() => store.AddEntry(123, DiaryLevels.Normal, "x", "", "", folder.Now.AddMinutes(10)));
        for (var i = 0; i < 3; i++) store.AddEntry(123, DiaryLevels.Normal, "bounded-" + i, "", "");
        Reject(() => store.DeleteEntry(123, "not-a-uuid"));
        Check(store.DeleteEntry(123, store.Get(123).Entries[0].Id), "valid diary entry was not deleted");
        Check(store.Get(123).Entries.Count == 2, "deleting a diary entry removed the wrong count");
        System.IO.Directory.CreateDirectory(folder.Directory);
        File.WriteAllText(folder.Doc(555), "{\"version\":1}");
        try { store.Get(555); throw new Exception("damaged companion file was accepted"); }
        catch (InvalidDataException) { }
        return Task.CompletedTask;
    }

    private static Task Isolation()
    {
        using var folder = new Folder();
        var store = folder.Store();
        store.SetMood(123, "开心");
        store.AddEntry(123, DiaryLevels.Important, "约定的秘密", "", "");
        store.SetMood(456, "难过");
        var mine = store.BuildContext(123);
        Check(mine.Contains("开心", StringComparison.Ordinal) && mine.Contains("约定的秘密", StringComparison.Ordinal), "trigger contact's state was missing");
        Check(!mine.Contains("难过", StringComparison.Ordinal), "another contact's mood leaked into the context");
        Check(store.BuildContext(456).Contains("难过", StringComparison.Ordinal), "second contact's mood was missing");
        return Task.CompletedTask;
    }

    private static async Task WakeLoop()
    {
        using var folder = new Folder();
        var store = folder.Store();
        var contacts = new ContactContextStore(folder.Root, () => folder.Now);
        var clock = new FixedTime(folder.Now);
        store.SaveSettings(new CompanionSettings { Enabled = true, AuditHour = LocalHour(folder.Now), IdleMinutes = 120,
            CooldownMinutes = 120, DailyLimit = 2 });
        store.MarkAudited(123, folder.Now);
        contacts.AppendMessage(123, "user", "我先睡啦", "m1", folder.Now.AddHours(-3));
        var delivered = new List<string>();
        var calls = 0;
        var runtime = Runtime(store, contacts, clock, () => { calls++; }, delivered, text: "{\"send\":true,\"text\":\"睡不着，想你了\"}");
        await runtime.TickAsync(CancellationToken.None);
        Check(delivered.SequenceEqual(["睡不着，想你了"]) && calls == 1, "wake loop did not deliver the decided message");
        Check(store.Get(123).WakeupsToday == 1 && store.Get(123).LastWakeupAt is not null, "wake loop did not count its wakeup");
        await runtime.TickAsync(CancellationToken.None);
        Check(delivered.Count == 1 && calls == 1, "wake loop ignored the cooldown");
        folder.Now = folder.Now.AddHours(3);
        clock.Now = folder.Now;
        await runtime.TickAsync(CancellationToken.None);
        Check(delivered.Count == 2 && store.Get(123).WakeupsToday == 2, "wake loop stopped before the daily limit");
        folder.Now = folder.Now.AddHours(3);
        clock.Now = folder.Now;
        await runtime.TickAsync(CancellationToken.None);
        Check(delivered.Count == 2, "wake loop exceeded the daily limit");
    }

    private static async Task WakeLoopQuiet()
    {
        foreach (var (declaration, paused, busy, pending) in new[]
                 {
                     ("模型选择不打扰", false, false, 0), ("暂停时不主动", true, false, 0),
                     ("正在回复时不主动", false, true, 0), ("有待处理消息时不主动", false, false, 1)
                 })
        {
            using var folder = new Folder();
            var store = folder.Store();
            var contacts = new ContactContextStore(folder.Root, () => folder.Now);
            var clock = new FixedTime(folder.Now);
            store.SaveSettings(new CompanionSettings { Enabled = true, AuditHour = LocalHour(folder.Now), IdleMinutes = 120,
                CooldownMinutes = 120, DailyLimit = 2 });
            store.MarkAudited(123, folder.Now);
            contacts.AppendMessage(123, "user", "晚安", "m1", folder.Now.AddHours(-5));
            var delivered = new List<string>();
            var calls = 0;
            var runtime = Runtime(store, contacts, clock, () => { calls++; }, delivered,
                text: declaration == "模型选择不打扰" ? "{\"send\":false,\"text\":\"\"}" : "{\"send\":true,\"text\":\"在吗\"}",
                paused: () => paused, busy: () => busy, pending: () => pending);
            await runtime.TickAsync(CancellationToken.None);
            Check(delivered.Count == 0 && store.Get(123).WakeupsToday == 0, "wake loop spoke when it should stay quiet: " + declaration);
            Check(declaration != "模型选择不打扰" || calls == 1, "declining model was never asked: " + declaration);
        }
    }

    private static async Task DailyAudit()
    {
        using var folder = new Folder();
        var store = folder.Store();
        var contacts = new ContactContextStore(folder.Root, () => folder.Now);
        var clock = new FixedTime(folder.Now);
        // The upcoming local hour keeps today's audit due; a zero daily limit keeps the wake path out of the way.
        store.SaveSettings(new CompanionSettings { Audit = true, Enabled = true, AuditHour = (LocalHour(folder.Now) + 1) % 24,
            IdleMinutes = 120, CooldownMinutes = 120, DailyLimit = 0 });
        contacts.AppendMessage(20, "user", "今天面试完了，还行", "m1", folder.Now.AddHours(-2));
        contacts.AppendMessage(20, "assistant", "我就知道你可以", "m2", folder.Now.AddHours(-2).AddMinutes(1));
        var delivered = new List<string>();
        var calls = 0;
        var runtime = Runtime(store, contacts, clock, () => { calls++; }, delivered,
            text: "{\"mood\":\"有点骄傲\",\"entries\":[{\"level\":\"important\",\"text\":\"他面试完了\",\"moodAtTime\":\"紧张又骄傲\",\"unsaid\":\"其实我一直在等你消息\"}]}",
            user: 20);
        await runtime.TickAsync(CancellationToken.None);
        var snapshot = store.Get(20);
        Check(calls == 1 && snapshot.Entries.Count == 1 && snapshot.Mood == "有点骄傲", "daily audit did not write the diary or mood");
        Check(snapshot.Entries[0].Level == DiaryLevels.Important && snapshot.Entries[0].Unsaid.Length > 0, "daily audit lost the diary level or the unsaid line");
        Check(snapshot.LastAuditAt is not null && snapshot.WakeupsToday == 0, "daily audit marked the wrong state");
        folder.Now = folder.Now.AddHours(1);
        clock.Now = folder.Now;
        await runtime.TickAsync(CancellationToken.None);
        Check(calls == 1 && store.Get(20).Entries.Count == 1, "daily audit ran twice for the same day");
        store.SaveSettings(store.LoadSettings() with { Audit = false });
        folder.Now = folder.Now.AddDays(1);
        clock.Now = folder.Now;
        await runtime.TickAsync(CancellationToken.None);
        Check(calls == 1, "disabled diary audit still called the model");
    }

    private static CompanionRuntime Runtime(CompanionStore store, ContactContextStore contacts, FixedTime clock,
        Action onCall, List<string> delivered, string text, Func<bool>? paused = null, Func<bool>? busy = null,
        Func<int>? pending = null, long user = 123) =>
        new(store, contacts, new PersonaSettings { Name = "大肥鱼" },
            _ => { onCall(); return new FixedReply(text); },
            () => new GroupChatStatus(GroupChatMode.Active, paused?.Invoke() ?? false, 0, [])
            {
                PrivateContacts = [new PrivateChatContactStatus(user, busy?.Invoke() ?? false, false, null, 0, 0, 0)
                    { PendingMessages = pending?.Invoke() ?? 0 }]
            },
            (_, body, _) => { delivered.Add(body); return Task.CompletedTask; },
            [user], true, (_, _) => { }, clock);

    private static int LocalHour(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Hour;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedReply(string output) : IStreamingReply
    {
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            yield return output;
        }
    }

    private static Task Boundary()
    {
        var now = DateTimeOffset.Now;
        var boundary = CompanionStore.AuditBoundary(now, 4);
        Check(boundary <= now && now - boundary <= TimeSpan.FromDays(1), "audit boundary was not within the last day");
        Check(boundary.ToLocalTime().Hour == 4 && boundary.Minute == 0, "audit boundary ignored the configured hour");
        return Task.CompletedTask;
    }

    private static void Reject(string body)
    {
        try { CompanionJournal.Parse(body); }
        catch (InvalidDataException) { return; }
        throw new Exception("invalid journal draft was accepted");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException) { return; }
        throw new Exception("invalid companion operation was accepted");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Folder : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "audio-bridge-companion-check-" + Guid.NewGuid().ToString("N"));
        public DateTimeOffset Now { get; set; } = InitialNow;
        public string Directory => System.IO.Path.Combine(Root, "Results", "local", "companion");
        public string Doc(long userId) => System.IO.Path.Combine(Directory, "companion-" + userId + ".json");
        public Folder() => System.IO.Directory.CreateDirectory(Root);
        public CompanionStore Store() => new(Root, () => Now);
        public void Dispose() { if (System.IO.Directory.Exists(Root)) System.IO.Directory.Delete(Root, recursive: true); }
    }
}
