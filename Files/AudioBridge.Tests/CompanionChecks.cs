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
            ("affection and possession meters are clamped, editable and fed to replies", Meters),
            ("invalid entries, oversized values and contact caps are rejected", Bounds),
            ("only the trigger contact's state reaches a private reply context", Isolation),
            ("audit boundary follows the configured local hour", Boundary),
            ("wake loop sends once, respects cooldown and the daily limit", WakeLoop),
            ("wake loop stays quiet when paused, busy or the model declines", WakeLoopQuiet),
            ("space publishing happens once a day and respects the switch and the model", QzonePublication),
            ("new space comments are answered in private chat exactly once", QzoneCommentReplies),
            ("daily audit writes the diary once and updates the mood", DailyAudit),
            ("Shanghai midnight reopens a capped wake gate before any new write", ShanghaiRollover),
            ("journal batches cover older and same-time messages and resume after restart", AuditBatches),
            ("a failed later journal batch retains its exact progress for retry", AuditRetry),
            ("journal capacity failures leave mood entries and progress unchanged", AtomicAudit),
            ("legacy companion documents load without inventing audit progress", LegacyProgress),
            ("invalid wake and diary JSON are observed as failures and recover after backoff", ParserRecovery),
            ("wake model errors report failure and the next attempt can recover", ModelRecovery),
            ("external cancellation remains cancellation and never reports a successful tick", ExternalCancellation)
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
        var night = new DateTimeOffset(2026, 10, 21, 17, 30, 0, TimeSpan.Zero); // 01:30 Shanghai
        Check(!CompanionStore.ShouldWake(settings, CompanionSnapshot.Empty(1), night.AddHours(-3), night, false, 0), "wake gate opened during the night quiet hours");
        var morning = new DateTimeOffset(2026, 10, 22, 0, 30, 0, TimeSpan.Zero); // 08:30 Shanghai
        Check(CompanionStore.ShouldWake(settings, CompanionSnapshot.Empty(1), morning.AddHours(-3), morning, false, 0), "wake gate stayed closed after the night quiet hours");
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
        var meters = CompanionJournal.Parse("{\"mood\":\"有点甜\",\"entries\":[],\"affectionDelta\":3,\"possessionDelta\":-2}");
        Check(meters.AffectionDelta == 3 && meters.PossessionDelta == -2, "journal meter deltas were dropped or misread");
        Check(CompanionJournal.Parse("{\"mood\":\"平静\",\"entries\":[]}").AffectionDelta == 0, "omitted meter deltas should default to zero");
        Reject("{\"mood\":\"平静\",\"entries\":[],\"affectionDelta\":4}");
        Check(CompanionJournal.Parse("{\"mood\":\"平静\",\"entries\":[],\"affectionDelta\":-3}").AffectionDelta == -3,
            "the allowed meter delta boundary was rejected");
        Reject("{\"mood\":\"平静\",\"entries\":[],\"possessionDelta\":1.5}");
        Reject("{\"mood\":\"平静\",\"entries\":[],\"possessionDelta\":\"2\"}");
        Reject("{\"mood\":\"平静\",\"entries\":[],\"extra\":\"y\"}");
        Reject("{\"entries\":[]}");
        Reject("[]");
        Reject("not json");
        return Task.CompletedTask;
    }

    private static Task Meters()
    {
        using var folder = new Folder();
        var store = folder.Store();
        var fresh = store.Get(123);
        Check(fresh.Affection == CompanionStore.DefaultAffection && fresh.Possession == CompanionStore.DefaultPossession,
            "a new contact did not start from the documented meter defaults");
        store.SetMeters(123, 80, 65);
        var saved = store.Get(123);
        Check(saved.Affection == 80 && saved.Possession == 65, "manual meters were not saved");
        var context = store.BuildContext(123);
        Check(context.Contains("【你此刻对他的感觉】", StringComparison.Ordinal) &&
            context.Contains(CompanionStore.DescribeAffection(80), StringComparison.Ordinal) &&
            context.Contains(CompanionStore.DescribePossession(65), StringComparison.Ordinal),
            "the reply context does not turn the meters into how she should behave");
        store.SetMeters(123, 900, 10);
        Check(store.BuildContext(123).Contains("非常喜欢他", StringComparison.Ordinal), "high affection did not read as very fond");
        store.SetMeters(123, -900, 10);
        Check(store.BuildContext(123).Contains("很烦他", StringComparison.Ordinal), "low affection did not read as annoyed");
        store.SetMeters(123, 80, 65);
        store.SetMeters(123, 999, 99);
        store.NudgeMeters(123, 3, 3);
        var clamped = store.Get(123);
        Check(clamped.Affection == 1000 && clamped.Possession == 100, "meter changes were not clamped to the documented ranges");
        store.SetMeters(123, -999, 1);
        store.NudgeMeters(123, -3, -3);
        Check(store.Get(123).Affection == -1000 && store.Get(123).Possession == 0, "negative affection was not clamped at -1000");
        Reject(() => store.SetMeters(123, 1001, 0));
        Reject(() => store.SetMeters(123, -1001, 0));
        Reject(() => store.SetMeters(123, 0, -1));
        Reject(() => store.NudgeMeters(123, 4, 0));
        var cursor = new CompanionAuditCursor(Guid.NewGuid().ToString("D"), folder.Now);
        store.SetMeters(123, 50, 50);
        store.ApplyAuditBatch(123, new JournalDraft("心情", [], 9, -9), cursor, folder.Now);
        var audited = store.Get(123);
        Check(audited.Affection == 53 && audited.Possession == 47,
            "a journal batch did not cap its meters at plus or minus three");
        folder.Now = folder.Now.AddHours(1);
        store.ApplyAuditBatch(123, new JournalDraft("心情", [], -2, 1), new CompanionAuditCursor(Guid.NewGuid().ToString("D"), folder.Now), folder.Now);
        var moved = store.Get(123);
        Check(moved.Affection == 51 && moved.Possession == 48, "journal deltas were not applied to the meters");
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
        // 群聊也带态度，但不带私人日记。
        var group = GroupChatRuntime.CompanionReference(new ParsedGroupMessage(1095601728, 123, 9002, folder.Now, "在吗", false, false), store);
        Check(group.Contains("【你此刻对他的感觉】", StringComparison.Ordinal) &&
            !group.Contains("约定的秘密", StringComparison.Ordinal) && !group.Contains("近期日记", StringComparison.Ordinal),
            "group triggers must carry the meters but never the private diary");
        var priv = GroupChatRuntime.CompanionReference(new ParsedGroupMessage(0, 123, 9003, folder.Now, "在吗", false, false, IsPrivate: true), store);
        Check(priv.Contains("约定的秘密", StringComparison.Ordinal), "private triggers lost the diary");
        Check(GroupChatRuntime.CompanionReference(null, store).Length == 0, "no trigger should add no companion context");
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
        var prompts = new List<string>();
        var calls = 0;
        var runtime = Runtime(store, contacts, clock, () => { calls++; }, delivered, "",
            replyFactory: prompt => { prompts.Add(prompt); return new FixedReply("{\"send\":true,\"text\":\"睡不着，想你了\"}"); });
        await runtime.TickAsync(CancellationToken.None);
        Check(delivered.SequenceEqual(["睡不着，想你了"]) && calls == 1, "wake loop did not deliver the decided message");
        Check(prompts.Count == 1 && prompts[0].Contains("睡没睡", StringComparison.Ordinal) &&
              prompts[0].Contains("我先睡啦", StringComparison.Ordinal) && prompts[0].Contains("现在：", StringComparison.Ordinal),
            "wake prompt missed the sleep rule, the clock or the recent transcript");
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

    private static async Task QzoneCommentReplies()
    {
        using var folder = new Folder();
        var store = folder.Store();
        var contacts = new ContactContextStore(folder.Root, () => folder.Now);
        store.SaveSettings(new CompanionSettings { Audit = false, Enabled = false, Qzone = true });
        var comments = new List<QzoneComment> { new("t1:1", "t1", 99, 29070481, "示熠", "第一条历史评论", "01:15") };
        var delivered = new List<string>();
        var clock = new FixedTime(folder.Now);
        var calls = 0;
        var runtime = Runtime(store, contacts, clock, () => { calls++; }, delivered, "",
            user: 29070481,
            replyFactory: _ => new InspectReply((_, _) => "{\"send\":true,\"text\":\"嘿嘿 学会啦\"}"),
            qzoneComments: _ => Task.FromResult<IReadOnlyList<QzoneComment>>(comments.ToArray()),
            qzone: new QzoneStore(folder.Root, () => folder.Now));
        await runtime.TickAsync(CancellationToken.None);
        Check(delivered.Count == 0 && calls == 0, "historical space comments were answered on the first poll");
        folder.Now = folder.Now.AddMinutes(11);
        clock.Now = folder.Now;
        comments.Add(new("t1:2", "t1", 99, 29070481, "示熠", "小鱼你会发空间啦", "11:30"));
        comments.Add(new("t1:3", "t1", 99, 2515923747, "小糯米团子", "别人的评论", "11:31"));
        comments.Add(new("t1:4", "t1", 99, 1667533356, "大肥鱼", "我自己回自己", "11:32"));
        await runtime.TickAsync(CancellationToken.None);
        Check(delivered.SequenceEqual(["嘿嘿 学会啦"]) && calls == 1, "new space comment was not answered once in private chat");
        folder.Now = folder.Now.AddMinutes(11);
        clock.Now = folder.Now;
        await runtime.TickAsync(CancellationToken.None);
        Check(delivered.Count == 1 && calls == 1, "the same space comment was answered twice");
        using var declineFolder = new Folder();
        var declineStore = declineFolder.Store();
        declineStore.SaveSettings(new CompanionSettings { Audit = false, Enabled = false, Qzone = true });
        var declineComments = new List<QzoneComment> { new("t2:1", "t2", 99, 29070481, "示熠", "历史", "01:15") };
        var declineDelivered = new List<string>();
        var declineClock = new FixedTime(declineFolder.Now);
        var declineCalls = 0;
        var declining = Runtime(declineStore, new ContactContextStore(declineFolder.Root, () => declineFolder.Now), declineClock,
            () => { declineCalls++; }, declineDelivered, "",
            user: 29070481,
            replyFactory: _ => new InspectReply((_, _) => "{\"send\":false,\"text\":\"\"}"),
            qzoneComments: _ => Task.FromResult<IReadOnlyList<QzoneComment>>(declineComments.ToArray()),
            qzone: new QzoneStore(declineFolder.Root, () => declineFolder.Now));
        await declining.TickAsync(CancellationToken.None);
        declineFolder.Now = declineFolder.Now.AddMinutes(11);
        declineClock.Now = declineFolder.Now;
        declineComments.Add(new("t2:2", "t2", 99, 29070481, "示熠", "这句不回", "11:30"));
        await declining.TickAsync(CancellationToken.None);
        declineFolder.Now = declineFolder.Now.AddMinutes(11);
        declineClock.Now = declineFolder.Now;
        await declining.TickAsync(CancellationToken.None);
        Check(declineDelivered.Count == 0 && declineCalls == 1, "a declined comment reply was retried or sent");
    }

    private static async Task QzonePublication()
    {
        using var folder = new Folder();
        var store = folder.Store();
        var contacts = new ContactContextStore(folder.Root, () => folder.Now);
        store.SaveSettings(new CompanionSettings { Audit = false, Enabled = false, Qzone = true });
        folder.Now = QzoneSchedule.Target(folder.Now).AddMinutes(1);
        var clock = new FixedTime(folder.Now);
        var published = new List<string>();
        var calls = 0;
        var runtime = Runtime(store, contacts, clock, () => { calls++; }, [], "",
            replyFactory: prompt => new InspectReply((_, _) => "{\"send\":true,\"text\":\"今天也要记得好好吃饭呀\"}"),
            publishQzone: (text, _) => { published.Add(text); return Task.CompletedTask; },
            qzone: new QzoneStore(folder.Root, () => folder.Now));
        await runtime.TickAsync(CancellationToken.None);
        Check(published.SequenceEqual(["今天也要记得好好吃饭呀"]) && calls == 1, "space publish did not run or sent the wrong text");
        await runtime.TickAsync(CancellationToken.None);
        Check(published.Count == 1 && calls == 1, "space published twice in one day");
        store.SaveSettings(store.LoadSettings() with { Qzone = false });
        folder.Now = folder.Now.AddDays(1);
        clock.Now = folder.Now;
        await runtime.TickAsync(CancellationToken.None);
        Check(published.Count == 1 && calls == 1, "disabled switch still published");
        store.SaveSettings(store.LoadSettings() with { Qzone = true });
        folder.Now = QzoneSchedule.Target(folder.Now).AddMinutes(1);
        clock.Now = folder.Now;
        await runtime.TickAsync(CancellationToken.None);
        Check(calls == 2, "re-enabled publishing never asked the model again");
        store.SaveSettings(store.LoadSettings() with { Qzone = true });
        using var declining = new Folder();
        var quietStore = declining.Store();
        quietStore.SaveSettings(new CompanionSettings { Audit = false, Enabled = false, Qzone = true });
        declining.Now = QzoneSchedule.Target(declining.Now).AddMinutes(1);
        var quietCalls = 0;
        var quiet = Runtime(quietStore, new ContactContextStore(declining.Root, () => declining.Now),
            new FixedTime(declining.Now), () => { quietCalls++; }, [], "",
            replyFactory: _ => new InspectReply((_, _) => "{\"send\":false,\"text\":\"\"}"),
            publishQzone: (_, _) => throw new Exception("declined post was published"),
            qzone: new QzoneStore(declining.Root, () => declining.Now));
        await quiet.TickAsync(CancellationToken.None);
        await quiet.TickAsync(CancellationToken.None);
        Check(quietCalls == 1, "a declined space post was retried the same day");
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

    private static async Task ShanghaiRollover()
    {
        using var folder = new Folder();
        var store = folder.Store();
        // UTC midnight is 08:00 in Shanghai, so it must not reset the Shanghai daily counter.
        folder.Now = new DateTimeOffset(2026, 10, 21, 23, 59, 0, TimeSpan.Zero);
        store.MarkWoken(456);
        folder.Now = new DateTimeOffset(2026, 10, 22, 0, 1, 0, TimeSpan.Zero);
        Check(store.Get(456).WakeupsToday == 1, "UTC midnight incorrectly reset the Shanghai daily counter");
        folder.Now = new DateTimeOffset(2026, 10, 21, 13, 0, 0, TimeSpan.Zero);
        var contacts = new ContactContextStore(folder.Root, () => folder.Now);
        var clock = new FixedTime(folder.Now);
        store.SaveSettings(new CompanionSettings { Audit = false, Enabled = true, DailyLimit = 1, CooldownMinutes = 30 });
        contacts.AppendMessage(123, "user", "晚安", "m1", folder.Now.AddHours(-3));
        var delivered = new List<string>();
        var runtime = Runtime(store, contacts, clock, () => { }, delivered, "{\"send\":true,\"text\":\"想你了\"}");
        await runtime.TickAsync(CancellationToken.None);
        var capped = store.Get(123);
        Check(capped.WakeupsToday == 1, "fixture never reached its daily cap");
        await runtime.TickAsync(CancellationToken.None);
        Check(delivered.Count == 1, "same-day cap was ignored");
        folder.Now = new DateTimeOffset(2026, 10, 21, 16, 1, 0, TimeSpan.Zero);
        clock.Now = folder.Now;
        Check(store.Get(123).WakeupsToday == 0 && store.All().Single(item => item.UserId == 123).WakeupsToday == 0,
            "read retained the previous Shanghai day's count");
        Check(!CompanionStore.ShouldWake(store.LoadSettings(), capped, folder.Now.AddHours(-5), folder.Now, false, 0),
            "night quiet hours woke the contact after Shanghai midnight");
        await runtime.TickAsync(CancellationToken.None);
        Check(delivered.Count == 1, "night quiet hours still sent a message");
        folder.Now = new DateTimeOffset(2026, 10, 22, 0, 30, 0, TimeSpan.Zero); // 08:30 Shanghai
        clock.Now = folder.Now;
        Check(CompanionStore.ShouldWake(store.LoadSettings(), capped, folder.Now.AddHours(-5), folder.Now, false, 0),
            "a previously read snapshot stayed capped across Shanghai midnight");
        await runtime.TickAsync(CancellationToken.None);
        Check(delivered.Count == 2 && store.Get(123).WakeupsToday == 1, "next-day send gate stayed closed");
        Check(CompanionStore.AuditBoundary(new DateTimeOffset(2026, 10, 21, 21, 0, 0, TimeSpan.Zero), 4) ==
              new DateTimeOffset(2026, 10, 22, 4, 0, 0, TimeSpan.FromHours(8)), "audit boundary depended on the machine timezone");
    }

    private static async Task AuditBatches()
    {
        using var folder = new Folder();
        var store = folder.Store();
        var contacts = new ContactContextStore(folder.Root, () => folder.Now);
        var clock = new FixedTime(folder.Now);
        store.SaveSettings(new CompanionSettings { Audit = true });
        var timestamp = folder.Now.AddMinutes(-5);
        for (var index = 0; index < 230; index++)
            contacts.AppendMessage(123, "user", "mark" + index.ToString("D3") + ":" + new string('x', 70), "m" + index, timestamp);
        var sources = new List<string>();
        CompanionRuntime Create() => Runtime(folder.Store(), contacts, clock, () => { }, [], "",
            replyFactory: _ => new InspectReply((history, _) =>
            {
                sources.Add(history.Single().Content);
                return "{\"mood\":\"平静\",\"entries\":[]}";
            }));
        var runtime = Create();
        await runtime.TickAsync(CancellationToken.None);
        Check(store.Get(123) is { AuditPending: true, AuditCursor: not null, LastAuditAt: null },
            "first truncated batch marked the whole audit complete");
        runtime = Create();
        for (var attempt = 0; attempt < 5 && store.Get(123).AuditPending; attempt++)
            await runtime.TickAsync(CancellationToken.None);
        Check(!store.Get(123).AuditPending && store.Get(123).LastAuditAt == folder.Now && sources.Count > 1,
            "batched audit never completed after rebuilding its runtime");
        var joined = string.Join("", sources);
        for (var index = 0; index < 230; index++)
        {
            var marker = "mark" + index.ToString("D3") + ":";
            Check(joined.Split(marker, StringSplitOptions.None).Length == 2, "source was skipped or repeated: " + marker);
        }
        Check(sources.All(source => source.Length <= CompanionJournal.MaximumSourceCharacters), "a journal batch exceeded its character limit");
        contacts.AppendMessage(123, "user", "late-same-timestamp", "later", timestamp);
        folder.Now = folder.Now.AddDays(1);
        clock.Now = folder.Now;
        await runtime.TickAsync(CancellationToken.None);
        Check(sources[^1].Contains("late-same-timestamp", StringComparison.Ordinal) &&
              !sources[^1].Contains("mark", StringComparison.Ordinal), "late same-time message was lost or earlier messages replayed");
    }

    private static async Task AuditRetry()
    {
        using var folder = new Folder();
        var store = folder.Store();
        var contacts = new ContactContextStore(folder.Root, () => folder.Now);
        var clock = new FixedTime(folder.Now);
        store.SaveSettings(new CompanionSettings { Audit = true });
        for (var index = 0; index < 20; index++)
            contacts.AppendMessage(123, "user", "retry" + index.ToString("D2") + new string('x', 990), "m" + index, folder.Now.AddMinutes(-5));
        var calls = 0;
        var successful = new List<string>();
        var runtime = Runtime(store, contacts, clock, () => { }, [], "", replyFactory: _ => new InspectReply((history, _) =>
        {
            calls++;
            if (calls == 2) throw new JsonException("invalid fixture response");
            successful.Add(history.Single().Content);
            return "{\"mood\":\"平静\",\"entries\":[]}";
        }));
        await runtime.TickAsync(CancellationToken.None);
        var progress = store.Get(123).AuditCursor;
        await runtime.TickAsync(CancellationToken.None);
        Check(store.Get(123).AuditCursor == progress && store.Get(123).AuditPending && store.Get(123).LastAuditAt is null,
            "failed second batch advanced or discarded progress");
        folder.Now = folder.Now.AddMinutes(11);
        clock.Now = folder.Now;
        await runtime.TickAsync(CancellationToken.None);
        Check(calls == 3 && !store.Get(123).AuditPending, "later journal batch did not recover after backoff");
        var joined = string.Join("", successful);
        for (var index = 0; index < 20; index++)
            Check(joined.Split("retry" + index.ToString("D2"), StringSplitOptions.None).Length == 2, "successful batch replayed or omitted a message");
    }

    private static Task AtomicAudit()
    {
        using var folder = new Folder();
        var store = folder.Store();
        store.SetMood(123, "原来的心情");
        var entry = store.AddEntry(123, DiaryLevels.Vital, "原来的日记", "", "");
        var path = folder.Doc(123);
        using (var source = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var document = System.Text.Json.Nodes.JsonNode.Parse(source.RootElement.GetRawText())!;
            document["entries"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 199)
                .Select(_ => entry with { Id = Guid.NewGuid().ToString("D") }),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(path, document.ToJsonString());
        }
        var before = File.ReadAllBytes(path);
        var draft = new JournalDraft("新心情", [new(DiaryLevels.Normal, "新日记1", "", ""), new(DiaryLevels.Normal, "新日记2", "", "")]);
        Reject(() => store.ApplyAuditBatch(123, draft, new(Guid.NewGuid().ToString("D"), folder.Now), folder.Now));
        Check(File.ReadAllBytes(path).SequenceEqual(before), "capacity failure partially wrote diary mood or progress");
        Check(store.Get(123) is { AuditCursor: null, LastAuditAt: null, Mood: "原来的心情" }, "failed atomic write changed observable state");
        using var lockedFolder = new Folder();
        var lockedStore = lockedFolder.Store();
        lockedStore.SetMood(123, "原心情");
        lockedStore.AddEntry(123, DiaryLevels.Vital, "原日记", "", "");
        var lockedPath = lockedFolder.Doc(123);
        var lockedBefore = File.ReadAllBytes(lockedPath);
        var next = new JournalDraft("新心情", [new(DiaryLevels.Normal, "新日记", "", "")]);
        var cursor = new CompanionAuditCursor(Guid.NewGuid().ToString("D"), lockedFolder.Now);
        using (var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { lockedStore.ApplyAuditBatch(123, next, cursor, lockedFolder.Now); throw new Exception("locked replacement unexpectedly succeeded"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Check(File.ReadAllBytes(lockedPath).SequenceEqual(lockedBefore), "failed file replacement partially changed journal state");
        lockedStore.ApplyAuditBatch(123, next, cursor, lockedFolder.Now);
        Check(lockedStore.Get(123) is { Mood: "新心情", AuditPending: false } after && after.AuditCursor == cursor &&
              after.LastAuditAt == lockedFolder.Now && after.Entries.Count == 2, "retry did not commit diary mood and progress together");
        return Task.CompletedTask;
    }

    private static Task LegacyProgress()
    {
        using var folder = new Folder();
        System.IO.Directory.CreateDirectory(folder.Directory);
        File.WriteAllText(folder.Doc(123), "{\"version\":1,\"userId\":123,\"mood\":\"旧心情\",\"entries\":[]}");
        var snapshot = folder.Store().Get(123);
        Check(snapshot is { Mood: "旧心情", AuditCursor: null, AuditPending: false }, "legacy document failed optional progress migration");
        return Task.CompletedTask;
    }

    private static async Task ModelRecovery()
    {
        foreach (var error in new Exception[] { new JsonException(), new HttpRequestException(), new OperationCanceledException() })
        {
            using var folder = new Folder();
            var store = folder.Store();
            var contacts = new ContactContextStore(folder.Root, () => folder.Now);
            var clock = new FixedTime(folder.Now);
            store.SaveSettings(new CompanionSettings { Audit = false, Enabled = true });
            contacts.AppendMessage(123, "user", "晚安", "m1", folder.Now.AddHours(-5));
            var outcomes = new List<Exception?>();
            var calls = 0;
            var runtime = Runtime(store, contacts, clock, () => { }, [], "", replyFactory: _ => new InspectReply((_, _) =>
            {
                if (++calls == 1) throw error;
                return "{\"send\":false,\"text\":\"\"}";
            }), tickCompleted: outcomes.Add);
            await runtime.TickAsync(CancellationToken.None);
            Check(outcomes.Single() == error && store.Get(123).WakeupsToday == 0, "model error escaped or was reported as a quiet success");
            await runtime.TickAsync(CancellationToken.None);
            Check(calls == 1 && outcomes.Count == 1, "backoff was reported as a successful model recovery");
            folder.Now = folder.Now.AddMinutes(11);
            clock.Now = folder.Now;
            await runtime.TickAsync(CancellationToken.None);
            Check(calls == 2 && outcomes.Count == 2 && outcomes[^1] is null, "model error prevented the next successful quiet wake attempt");
        }
    }

    private static async Task ParserRecovery()
    {
        foreach (var audit in new[] { false, true })
        {
            using var folder = new Folder();
            var store = folder.Store();
            var contacts = new ContactContextStore(folder.Root, () => folder.Now);
            var clock = new FixedTime(folder.Now);
            store.SaveSettings(new CompanionSettings { Audit = audit, Enabled = !audit });
            contacts.AppendMessage(123, "user", "晚安", "m1", folder.Now.AddHours(-5));
            var outcomes = new List<Exception?>();
            var calls = 0;
            var runtime = Runtime(store, contacts, clock, () => { }, [], "", replyFactory: _ => new InspectReply((_, _) =>
                ++calls == 1 ? "not json" : audit ? "{\"mood\":\"平静\",\"entries\":[]}" : "{\"send\":false,\"text\":\"\"}"),
                tickCompleted: outcomes.Add);
            await runtime.TickAsync(CancellationToken.None);
            Check(outcomes.Single() is InvalidDataException && store.Get(123).LastAuditAt is null && store.Get(123).AuditCursor is null,
                "malformed model JSON was not reported without advancing progress");
            folder.Now = folder.Now.AddMinutes(11);
            clock.Now = folder.Now;
            await runtime.TickAsync(CancellationToken.None);
            Check(calls == 2 && outcomes.Count == 2 && outcomes[^1] is null, "malformed JSON prevented the next successful tick");
            Check(!audit || store.Get(123).AuditCursor is not null, "valid journal retry never committed its progress");
        }
    }

    private static async Task ExternalCancellation()
    {
        using var folder = new Folder();
        using var stop = new CancellationTokenSource();
        var store = folder.Store();
        var contacts = new ContactContextStore(folder.Root, () => folder.Now);
        var clock = new FixedTime(folder.Now);
        store.SaveSettings(new CompanionSettings { Audit = false, Enabled = true });
        contacts.AppendMessage(123, "user", "晚安", "m1", folder.Now.AddHours(-5));
        var outcomes = new List<Exception?>();
        var runtime = Runtime(store, contacts, clock, () => { }, [], "", replyFactory: _ => new InspectReply((_, token) =>
        {
            stop.Cancel();
            token.ThrowIfCancellationRequested();
            return "{\"send\":true,\"text\":\"不应发送\"}";
        }), tickCompleted: outcomes.Add);
        try { await runtime.TickAsync(stop.Token); throw new Exception("external cancellation was swallowed"); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Check(outcomes.Count == 0 && store.Get(123).WakeupsToday == 0, "cancelled tick reported success or advanced wake state");
    }

    private static CompanionRuntime Runtime(CompanionStore store, ContactContextStore contacts, FixedTime clock,
        Action onCall, List<string> delivered, string text, Func<bool>? paused = null, Func<bool>? busy = null,
        Func<int>? pending = null, long user = 123, Func<string, IStreamingReply>? replyFactory = null,
        Action<Exception?>? tickCompleted = null, Func<string, CancellationToken, Task>? publishQzone = null, QzoneStore? qzone = null,
        Func<CancellationToken, Task<IReadOnlyList<QzoneComment>>>? qzoneComments = null) =>
        new(store, contacts, new PersonaSettings { Name = "大肥鱼" },
            prompt => { onCall(); return replyFactory?.Invoke(prompt) ?? new FixedReply(text); },
            () => new GroupChatStatus(GroupChatMode.Active, paused?.Invoke() ?? false, 0, [])
            {
                PrivateContacts = [new PrivateChatContactStatus(user, busy?.Invoke() ?? false, false, null, 0, 0, 0)
                    { PendingMessages = pending?.Invoke() ?? 0 }]
            },
            (_, body, _) => { delivered.Add(body); return Task.CompletedTask; },
            [user], true, (_, _) => { }, clock, tickCompleted, publishQzone, qzone, qzoneComments);

    private static int LocalHour(DateTimeOffset now) => CompanionStore.ShanghaiTime(now).Hour;

    private sealed class InspectReply(Func<IReadOnlyList<ChatTurn>, CancellationToken, string> output) : IStreamingReply
    {
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            yield return output(history, ct);
        }
    }

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
        Check(CompanionStore.ShanghaiTime(boundary).Hour == 4 && boundary.Minute == 0, "audit boundary ignored the configured Shanghai hour");
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
