using System.Text.Json;
using System.Text.Json.Nodes;
using AudioBridge;

public static class ContactContextChecks
{
    private static readonly DateTimeOffset InitialNow = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("empty history and unapproved public preference create no files", Defaults),
            ("private transcript persistence and latest chronological selection", Persistence),
            ("seven-day retention removes expired raw text on reads and appends", Retention),
            ("global expiry isolates corrupt contacts and never reads public files", PurgeAllContacts),
            ("role and message-id deduplication persists only inside retention", Deduplication),
            ("private contacts and explicit history deletion remain isolated", IsolationAndDelete),
            ("explicit history deletion can remove corrupt raw text safely", DeleteCorruptHistory),
            ("group preference reads only manually approved fixed public fields", PublicPreferences),
            ("damaged private history cannot leak into group or other contacts", CorruptionIsolation),
            ("concurrent store instances keep independent transcript writes", ConcurrentInstances),
            ("private context keeps the newest two days of chat inside 3000 characters", ContextBounds),
            ("invalid identifiers and public fields reject without writing", InvalidValues),
            ("record count, per-file bytes and contact file count stay bounded", ResourceBounds),
            ("image captions attach to the private [图片] placeholder once", ImageNotes),
            ("received images are stored for the panel and expire with the transcript", ImageFiles),
            ("group and private lines keep their source apart", Channels),
            ("linked history and directory paths cannot escape the project", LinkedPaths)
        };
        foreach (var check in checks)
        {
            await check.Run().WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine("PASS contact context: " + check.Name);
        }
        return checks.Length;
    }

    private static Task Defaults()
    {
        using var folder = new Folder();
        var store = folder.Store();
        Check(store.Recent(123).Count == 0 && store.BuildPrivateHistoryContext(123) == "" && store.BuildGroupPreferenceContext(123) == "", "empty store fabricated context");
        Check(store.GetPublicPreference(123) == new PublicContactPreference(123, "", "neutral", "normal", false), "public approval defaulted open");
        Check(store.PurgeExpiredHistory() == new HistoryPurgeResult(0, 0, 0, 0), "empty purge fabricated files or changes");
        store.DeleteHistory(123);
        Check(!Directory.Exists(folder.Directory), "read-only defaults or deleting absent history created storage");
        return Task.CompletedTask;
    }

    private static Task PurgeAllContacts()
    {
        using var folder = new Folder();
        var store = folder.Store();
        store.AppendMessage(123, "user", "expired-alpha-secret", "old", folder.Now.AddDays(-6));
        store.AppendMessage(123, "assistant", "retained-alpha", "new");
        store.AppendMessage(456, "user", "expired-beta-secret", "old", folder.Now.AddDays(-6));
        store.AppendMessage(789, "user", "retained-gamma", "new");
        store.AppendMessage(999, "user", "will-be-corrupt", "old", folder.Now.AddDays(-6));
        File.WriteAllText(folder.Private(999), "corrupt-private-file-must-remain");
        store.SavePublicPreference(new(123, "公开称呼", "friendly", "short", true));
        File.WriteAllText(folder.Public(456), "corrupt-public-file-must-not-be-read");
        var noncanonical = System.IO.Path.Combine(folder.Directory, "private-0123.json");
        File.WriteAllText(noncanonical, "unmanaged-noncanonical-file");
        var publicBefore = File.ReadAllBytes(folder.Public(123));
        var corruptPublicBefore = File.ReadAllBytes(folder.Public(456));
        var unchanged = File.ReadAllBytes(folder.Private(789));
        var damaged = File.ReadAllBytes(folder.Private(999));
        folder.Now = folder.Now.AddDays(2);
        var result = store.PurgeExpiredHistory();
        Check(result == new HistoryPurgeResult(4, 2, 2, 1), "global purge did not report each valid, changed and damaged contact accurately");
        Check(!File.Exists(folder.Private(456)) && !File.ReadAllText(folder.Private(123)).Contains("expired-alpha-secret", StringComparison.Ordinal), "global purge retained expired raw text");
        Check(store.Recent(123).Single().Text == "retained-alpha" && File.ReadAllBytes(folder.Private(789)).SequenceEqual(unchanged), "global purge changed live private messages");
        Check(File.ReadAllBytes(folder.Private(999)).SequenceEqual(damaged) &&
              File.ReadAllBytes(folder.Public(123)).SequenceEqual(publicBefore) && File.ReadAllBytes(folder.Public(456)).SequenceEqual(corruptPublicBefore), "global purge reset damaged history or touched public preferences");
        Check(File.ReadAllText(noncanonical) == "unmanaged-noncanonical-file", "purge interpreted a noncanonical path as a contact");
        Check(store.PurgeExpiredHistory() == new HistoryPurgeResult(3, 0, 0, 1), "repeat purge changed live data or hid damaged contact");
        Check(!System.IO.Directory.EnumerateFiles(folder.Directory, "*.tmp").Any(), "purge retained a private temporary backup");
        return Task.CompletedTask;
    }

    private static Task Channels()
    {
        using var folder = new Folder();
        var store = folder.Store();
        store.AppendMessage(123, "user", "私聊一句", "1", folder.Now.AddMinutes(-2));
        store.AppendMessage(123, "user", ContactContextStore.GroupMarker + "群里一句", "2", folder.Now.AddMinutes(-1));
        var all = store.Recent(123);
        Check(all.Count == 2 && ContactContextStore.ChannelOf(all[0]) == "private" &&
            ContactContextStore.ChannelOf(all[1]) == "group" && ContactContextStore.BodyOf(all[1]) == "群里一句",
            "the archive lost the source or kept the group marker in the body");
        var context = store.BuildPrivateHistoryContext(123);
        Check(context.Contains("用户（群聊）：群里一句", StringComparison.Ordinal) &&
            context.Contains("用户：私聊一句", StringComparison.Ordinal) &&
            !context.Contains("[群聊]", StringComparison.Ordinal),
            "the model context did not label the group line or still shows the raw marker");
        return Task.CompletedTask;
    }

    private static Task ImageNotes()
    {
        using var folder = new Folder();
        var store = folder.Store();
        Check(!store.AnnotateLatestImage(123, "没有图可标"), "an annotation claimed to write without an image message");
        store.AppendMessage(123, "user", "[图片]", "9", folder.Now.AddMinutes(-2));
        store.AppendMessage(123, "assistant", "好看", "10", folder.Now.AddMinutes(-1));
        Check(store.AnnotateLatestImage(123, "蓝绿双马尾的小家伙趴着\n枕头"), "the caption was not attached to the image message");
        Check(store.Recent(123).First(x => x.MessageId == "9").Text == "[图片]（图片内容：蓝绿双马尾的小家伙趴着 枕头）",
            "the caption text or its whitespace normalization is wrong");
        Check(!store.AnnotateLatestImage(123, "第二遍"), "the same image was annotated twice");
        store.AppendMessage(123, "user", "[图片]", "11");
        Check(store.AnnotateLatestImage(123, new string('长', 1200)) && store.Recent(123).All(x => x.Text.Length <= 1000),
            "a long caption overflowed the transcript text limit");
        // 群聊图和白名单联系人的私聊图存在同一个文件里，按消息ID标才不会标错行。
        store.AppendMessage(123, "user", "[群聊] [图片]", "21", folder.Now.AddMinutes(-1));
        store.AppendMessage(123, "user", "[图片]", "22", folder.Now.AddSeconds(-30));
        Check(store.AnnotateLatestImage(123, "群里的猫", 21) &&
              store.Recent(123).First(x => x.MessageId == "21").Text == "[群聊] [图片]（图片内容：群里的猫）",
            "a group image line was not annotated by its message id");
        Check(store.Recent(123).First(x => x.MessageId == "22").Text == "[图片]", "annotating by id touched another image line");
        Check(!store.AnnotateLatestImage(123, "群里的猫", 21), "the same group image was annotated twice");
        Check(store.AnnotateLatestImage(123, "私聊的图", 22), "a private image line was not annotated by its message id");
        return Task.CompletedTask;
    }

    private static Task ImageFiles()
    {
        using var folder = new Folder();
        var store = folder.Store();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };
        store.SaveImage(123, "77", "image/png", png);
        Check(store.HasImage(123, "77") && store.TryReadImage(123, "77", out var read, out var mime) &&
              read.SequenceEqual(png) && mime == "image/png", "a stored image could not be read back");
        Check(!store.HasImage(123, "78") && !store.TryReadImage(123, "78", out _, out _),
            "an unknown message reported a stored image");
        Reject(() => store.SaveImage(123, "../77", "image/png", png));
        Reject(() => store.SaveImage(123, "7a", "image/png", png));
        Reject(() => store.SaveImage(123, "77", "text/html", png));
        Reject(() => store.SaveImage(123, "77", "image/png", []));
        Reject(() => store.SaveImage(0, "77", "image/png", png));
        folder.Now = DateTimeOffset.UtcNow.AddDays(8);
        Check(!store.TryReadImage(123, "77", out _, out _), "an expired image was still readable");
        folder.Now = InitialNow;
        store.SaveImage(123, "79", "image/jpeg", png);
        store.DeleteHistory(123);
        Check(!store.HasImage(123, "79"), "deleting a contact's transcript left its images behind");
        return Task.CompletedTask;
    }

    private static Task Persistence()
    {
        using var folder = new Folder();
        var first = folder.Store();
        first.AppendMessage(123, "user", "合成第一条", "1", folder.Now.AddMinutes(-3));
        first.AppendMessage(123, "assistant", "合成第二条", "2", folder.Now.AddMinutes(-2));
        first.AppendMessage(123, "user", "合成第三条", "3", folder.Now.AddMinutes(-1));
        var second = folder.Store();
        var all = second.Recent(123);
        Check(all.Count == 3 && all.Select(x => x.MessageId).SequenceEqual(new[] { "1", "2", "3" }) && all.All(x => x.UserId == 123 && Guid.TryParseExact(x.Id, "D", out _)),
            "restart lost or reordered explicit private records");
        Check(second.Recent(123, 2).Select(x => x.MessageId).SequenceEqual(new[] { "2", "3" }), "latest-N selection did not return ascending recent entries");
        Check(System.IO.Directory.GetFiles(folder.Directory).SequenceEqual(new[] { folder.Private(123) }), "transcript write retained a temporary/backup file");
        return Task.CompletedTask;
    }

    private static Task Retention()
    {
        using var folder = new Folder();
        var store = folder.Store();
        store.AppendMessage(123, "user", "boundary-ignored", "boundary", folder.Now.AddDays(-7));
        store.AppendMessage(123, "user", "older-ignored", "older", folder.Now.AddDays(-8));
        Check(store.Recent(123).Count == 0 && !File.Exists(folder.Private(123)), "expired input was stored");
        store.AppendMessage(123, "user", "expires-after-read", "1", folder.Now.AddDays(-6));
        store.AppendMessage(123, "assistant", "retained-after-read", "2", folder.Now);
        folder.Now = folder.Now.AddDays(2);
        Check(store.Recent(123).Single().MessageId == "2", "read did not remove expired entry");
        Check(!File.ReadAllText(folder.Private(123)).Contains("expires-after-read", StringComparison.Ordinal), "read left expired raw text on disk");
        folder.Now = folder.Now.AddDays(6);
        store.AppendMessage(123, "user", "retained-after-append", "3");
        Check(store.Recent(123).Single().MessageId == "3" && !File.ReadAllText(folder.Private(123)).Contains("retained-after-read", StringComparison.Ordinal), "append did not purge expired raw text");
        folder.Now = folder.Now.AddDays(7);
        Check(store.Recent(123).Count == 0 && !File.Exists(folder.Private(123)), "all-expired transcript file was retained");
        return Task.CompletedTask;
    }

    private static Task Deduplication()
    {
        using var folder = new Folder();
        var store = folder.Store();
        store.AppendMessage(123, "user", "原始合成内容", "same");
        var before = File.ReadAllBytes(folder.Private(123));
        folder.Store().AppendMessage(123, "user", "不得覆盖原文", "same");
        Check(File.ReadAllBytes(folder.Private(123)).SequenceEqual(before), "duplicate rewrote private transcript");
        store.AppendMessage(123, "assistant", "同一消息触发的合成回复", "same");
        store.AppendMessage(456, "user", "另一联系人相同消息ID", "same");
        Check(store.Recent(123).Count == 2 && store.Recent(456).Count == 1, "deduplication crossed role/contact boundaries");
        folder.Now = folder.Now.AddDays(7);
        store.AppendMessage(123, "user", "七天后同ID的新合成记录", "same");
        Check(store.Recent(123).Single().Text == "七天后同ID的新合成记录", "expired deduplication suppressed a new retained event");
        return Task.CompletedTask;
    }

    private static Task IsolationAndDelete()
    {
        using var folder = new Folder();
        var store = folder.Store();
        var profile = new BotProfileStore(folder.Root);
        var manual = profile.AddMemory(123, "手动长期笔记保持独立");
        store.AppendMessage(123, "user", "private-alpha-secret", "1");
        store.AppendMessage(456, "user", "private-beta-secret", "1");
        store.SavePublicPreference(new(123, "合成称呼", "friendly", "short", true));
        var other = File.ReadAllBytes(folder.Private(456));
        var publicBefore = File.ReadAllBytes(folder.Public(123));
        Check(!store.BuildPrivateHistoryContext(123).Contains("private-beta-secret", StringComparison.Ordinal) &&
              !store.BuildPrivateHistoryContext(456).Contains("private-alpha-secret", StringComparison.Ordinal), "private context crossed contacts");
        store.DeleteHistory(123);
        Check(!File.Exists(folder.Private(123)) && store.Recent(123).Count == 0 && File.ReadAllBytes(folder.Private(456)).SequenceEqual(other), "history deletion damaged another contact");
        Check(File.ReadAllBytes(folder.Public(123)).SequenceEqual(publicBefore) && profile.ListMemories(123).Single() == manual, "raw history deletion changed public preferences or manual notes");
        Check(System.IO.Directory.GetFiles(folder.Directory).Length == 2, "history deletion retained an extra private backup");
        return Task.CompletedTask;
    }

    private static Task PublicPreferences()
    {
        using var folder = new Folder();
        var store = folder.Store();
        var persona = new BotProfileStore(folder.Root);
        persona.AddMemory(123, "private-manual-secret");
        store.AppendMessage(123, "user", "private-raw-secret", "1");
        store.SavePublicPreference(new(123, "合成同学", "friendly", "short", false));
        Check(store.BuildGroupPreferenceContext(123) == "", "unapproved fields were sent into group context");
        store.SavePublicPreference(new(123, "合成同学", "friendly", "short", true));
        File.WriteAllText(folder.Private(123), "corrupt-private-raw-secret");
        File.WriteAllText(System.IO.Path.Combine(folder.Root, "bot-profile.local.json"), "corrupt-private-manual-secret");
        var context = folder.Store().BuildGroupPreferenceContext(123);
        Check(context.Contains("友好", StringComparison.Ordinal) && context.Contains("简短", StringComparison.Ordinal) &&
              !context.Contains("private", StringComparison.OrdinalIgnoreCase) && !context.Contains("secret", StringComparison.OrdinalIgnoreCase), "group preference read or exposed private files");
        Check(store.BuildGroupPreferenceContext(456) == "", "public preference leaked to another sender");
        store.SavePublicPreference(new(123, "", "neutral", "normal", false));
        Check(store.BuildGroupPreferenceContext(123) == "", "revoking manual approval did not stop public context");
        return Task.CompletedTask;
    }

    private static Task DeleteCorruptHistory()
    {
        using var folder = new Folder();
        var store = folder.Store();
        var profile = new BotProfileStore(folder.Root);
        var note = profile.AddMemory(123, "手动长期笔记不随原文删除");
        store.AppendMessage(123, "user", "private-history-to-delete", "1");
        store.AppendMessage(456, "user", "other-contact-must-remain", "1");
        store.SavePublicPreference(new(123, "公开称呼", "formal", "normal", true));
        var publicBefore = File.ReadAllBytes(folder.Public(123));
        var otherBefore = File.ReadAllBytes(folder.Private(456));
        foreach (var damaged in new[] { "not JSON", "", "{\"userId\":456,\"messages\":null}" })
        {
            File.WriteAllText(folder.Private(123), damaged);
            RejectCorrupt(() => store.Recent(123));
            store.DeleteHistory(123);
            Check(!File.Exists(folder.Private(123)) && store.Recent(123).Count == 0, "explicit deletion could not remove damaged raw text");
            Check(File.ReadAllBytes(folder.Public(123)).SequenceEqual(publicBefore) && File.ReadAllBytes(folder.Private(456)).SequenceEqual(otherBefore) &&
                  profile.ListMemories(123).Single() == note, "corrupt raw deletion damaged unrelated stores or contacts");
        }
        store.DeleteHistory(123);
        Check(System.IO.Directory.GetFiles(folder.Directory).Length == 2, "explicit deletion retained a private backup");
        System.IO.Directory.CreateDirectory(folder.Private(123));
        try
        {
            RejectCorrupt(() => store.DeleteHistory(123));
            Check(System.IO.Directory.Exists(folder.Private(123)), "file deletion removed a directory at the history path");
        }
        finally { System.IO.Directory.Delete(folder.Private(123)); }
        return Task.CompletedTask;
    }

    private static Task CorruptionIsolation()
    {
        using var folder = new Folder();
        var store = folder.Store();
        store.AppendMessage(123, "user", "alpha", "1");
        store.AppendMessage(456, "user", "beta", "1");
        store.SavePublicPreference(new(123, "", "neutral", "normal", true));
        var original = File.ReadAllText(folder.Private(123));
        var invalid = new List<string> { "", "not JSON", "{}", "null", "{\"version\":1,\"version\":1}" };
        foreach (var edit in new Action<JsonObject>[]
        {
            x => x["userId"] = 456, x => x["version"] = 2, x => x.Remove("messages"), x => x["messages"] = null,
            x => x["messages"]![0]!["userId"] = 456, x => x["messages"]![0]!["role"] = "system",
            x => x["messages"]![0]!["text"] = null, x => x["messages"]![0]!["id"] = "bad",
            x => x["messages"]![0]!["at"] = folder.Now.AddDays(1),
            x => x["messages"]![0]!.AsObject().Remove("messageId"),
            x => x["messages"]!.AsArray().Add(x["messages"]![0]!.DeepClone()),
            x => x["messages"]!.AsArray().Add((JsonNode?)null)
        })
        { var node = JsonNode.Parse(original)!.AsObject(); edit(node); invalid.Add(node.ToJsonString()); }
        foreach (var content in invalid)
        {
            File.WriteAllText(folder.Private(123), content);
            var before = File.ReadAllBytes(folder.Private(123));
            RejectCorrupt(() => store.Recent(123));
            RejectCorrupt(() => store.AppendMessage(123, "user", "not-reset", "new"));
            Check(File.ReadAllBytes(folder.Private(123)).SequenceEqual(before), "corrupt private data was silently cleared");
            Check(store.Recent(456).Single().Text == "beta" && store.BuildGroupPreferenceContext(123).Length > 0, "one damaged contact blocked another contact or public preference");
        }
        File.WriteAllText(folder.Private(123), original);
        var publicOriginal = File.ReadAllText(folder.Public(123));
        var publicBad = JsonNode.Parse(publicOriginal)!.AsObject();
        publicBad["preference"]!.AsObject().Remove("approved");
        File.WriteAllText(folder.Public(123), publicBad.ToJsonString());
        RejectCorrupt(() => store.GetPublicPreference(123)); RejectCorrupt(() => store.BuildGroupPreferenceContext(123));
        RejectCorrupt(() => store.SavePublicPreference(new(123, "", "neutral", "normal", true)));
        Check(store.Recent(123).Single().Text == "alpha", "damaged public preference blocked private history");
        return Task.CompletedTask;
    }

    private static async Task ConcurrentInstances()
    {
        using var folder = new Folder();
        var stores = Enumerable.Range(0, 16).Select(_ => folder.Store()).ToArray();
        await Task.WhenAll(stores.Select((store, index) => Task.Run(() => store.AppendMessage(123, "user", "concurrent-" + index, index.ToString()))));
        var final = folder.Store();
        Check(final.Recent(123).Count == 16 && final.Recent(123).Select(x => x.MessageId).Distinct().Count() == 16, "cross-instance private append lost updates");
        await Task.WhenAll(stores.Select(store => Task.Run(() => store.AppendMessage(123, "assistant", "deduplicated", "reply"))));
        Check(final.Recent(123).Count == 17 && final.Recent(123).Count(x => x.MessageId == "reply") == 1, "concurrent deduplication failed");
    }

    private static Task ContextBounds()
    {
        using var folder = new Folder();
        var store = folder.Store();
        for (var i = 0; i < 20; i++) store.AppendMessage(123, i % 2 == 0 ? "user" : "assistant", "entry-" + i + ":" + new string('中', 980), i.ToString(), folder.Now.AddMinutes(i - 20));
        store.AppendMessage(123, "user", "stale-before-window", "old", folder.Now.AddDays(-3));
        var context = store.BuildPrivateHistoryContext(123);
        Check(context.Length <= 3000 && context.Contains("现在：", StringComparison.Ordinal) &&
              context.Contains("entry-19:", StringComparison.Ordinal) && !context.Contains("entry-0:", StringComparison.Ordinal) &&
              !context.Contains("stale-before-window", StringComparison.Ordinal),
            "private context did not keep the newest chat, the clock line or the two-day window");
        var excluded = store.BuildPrivateHistoryContext(123, new[] { "18", "19" });
        Check(!excluded.Contains("entry-18:", StringComparison.Ordinal) && !excluded.Contains("entry-19:", StringComparison.Ordinal) && excluded.Contains("entry-17:", StringComparison.Ordinal), "current-message exclusion failed");
        Check(store.Recent(123).Count == 21, "prompt clipping changed persisted raw history");
        return Task.CompletedTask;
    }

    private static Task InvalidValues()
    {
        using var folder = new Folder();
        var store = folder.Store();
        store.AppendMessage(123, "user", "original", "1");
        store.SavePublicPreference(new(123, "", "neutral", "normal", false));
        var before = File.ReadAllBytes(folder.Private(123));
        var publicBefore = File.ReadAllBytes(folder.Public(123));
        foreach (var id in new long[] { 0, -1, long.MinValue })
        {
            Reject(() => store.Recent(id)); Reject(() => store.AppendMessage(id, "user", "x", "2"));
            Reject(() => store.DeleteHistory(id)); Reject(() => store.GetPublicPreference(id)); Reject(() => store.BuildGroupPreferenceContext(id));
            Reject(() => store.SavePublicPreference(new(id, "", "neutral", "normal", true)));
        }
        foreach (var role in new[] { "", "system", "USER", null! }) Reject(() => store.AppendMessage(123, role, "x", "2"));
        foreach (var text in new[] { "", " ", new string('x', 1001), "a\0b", null! }) Reject(() => store.AppendMessage(123, "user", text, "2"));
        foreach (var messageId in new[] { "", " ", new string('x', 129), "a\nb", null! }) Reject(() => store.AppendMessage(123, "user", "x", messageId));
        Reject(() => store.AppendMessage(123, "user", "x", "2", default(DateTimeOffset)));
        Reject(() => store.AppendMessage(123, "user", "x", "2", folder.Now.AddMinutes(6)));
        Reject(() => store.Recent(123, 0)); Reject(() => store.Recent(123, 1001));
        foreach (var invalid in new[]
        {
            new PublicContactPreference(123, new string('x', 33), "neutral", "normal", true),
            new PublicContactPreference(123, "a\nb", "neutral", "normal", true),
            new PublicContactPreference(123, null!, "neutral", "normal", true),
            new PublicContactPreference(123, "", "freeform-private-note", "normal", true),
            new PublicContactPreference(123, "", "neutral", "freeform-private-note", true)
        }) Reject(() => store.SavePublicPreference(invalid));
        Reject(() => store.SavePublicPreference(null!));
        Check(File.ReadAllBytes(folder.Private(123)).SequenceEqual(before) && File.ReadAllBytes(folder.Public(123)).SequenceEqual(publicBefore), "invalid values changed stored data");
        return Task.CompletedTask;
    }

    private static Task ResourceBounds()
    {
        using var folder = new Folder();
        var store = folder.Store();
        System.IO.Directory.CreateDirectory(folder.Directory);
        var messages = Enumerable.Range(0, 1000).Select(i => new StoredPrivateMessage(Guid.NewGuid().ToString("D"), 123, "m" + i,
            "user", "synthetic-" + i, folder.Now.AddSeconds(i - 1000))).ToArray();
        File.WriteAllBytes(folder.Private(123), JsonSerializer.SerializeToUtf8Bytes(new { version = 1, userId = 123, messages }, Json));
        store.AppendMessage(123, "assistant", "newest", "new");
        var recent = store.Recent(123, 1000);
        Check(recent.Count == 1000 && recent[0].MessageId == "m1" && recent[^1].MessageId == "new", "per-contact count did not discard only oldest transcript");
        File.WriteAllBytes(folder.Private(123), new byte[8 * 1024 * 1024 + 1]);
        RejectCorrupt(() => store.Recent(123));
        Check(new FileInfo(folder.Private(123)).Length == 8 * 1024 * 1024 + 1, "oversized private file was reset");
        File.Delete(folder.Private(123));
        for (var i = 1; i <= 64; i++) store.AppendMessage(i, "user", "bounded", "1");
        Reject(() => store.AppendMessage(65, "user", "over-limit", "1"));
        Check(System.IO.Directory.EnumerateFiles(folder.Directory, "private-*.json").Count() == 64, "private file count exceeded 64");
        store.DeleteHistory(1); store.AppendMessage(65, "user", "released-capacity", "1");
        for (var i = 1; i <= 64; i++) store.SavePublicPreference(new(i, "", "neutral", "normal", false));
        Reject(() => store.SavePublicPreference(new(65, "", "neutral", "normal", false)));
        Check(System.IO.Directory.EnumerateFiles(folder.Directory, "public-*.json").Count() == 64, "public preference file count exceeded 64");
        return Task.CompletedTask;
    }

    private static Task LinkedPaths()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        outside.Store().AppendMessage(123, "user", "outside-secret", "1");
        System.IO.Directory.CreateDirectory(folder.Directory);
        var original = File.ReadAllBytes(outside.Private(123));
        var linked = false;
        try { File.CreateSymbolicLink(folder.Private(123), outside.Private(123)); linked = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        if (linked)
        {
            try
            {
                var store = folder.Store();
                RejectCorrupt(() => store.Recent(123)); RejectCorrupt(() => store.AppendMessage(123, "user", "overwrite", "2"));
                RejectCorrupt(() => store.DeleteHistory(123));
                Check(store.PurgeExpiredHistory() == new HistoryPurgeResult(1, 0, 0, 1), "purge followed or silently ignored a linked private file");
                Check(store.BuildGroupPreferenceContext(123) == "", "group preference touched a linked private file");
            }
            finally { File.Delete(folder.Private(123)); }
            Check(File.ReadAllBytes(outside.Private(123)).SequenceEqual(original), "linked private path changed outside data");
        }
        var link = System.IO.Path.Combine(folder.Root, "linked-project");
        var directoryLinked = false;
        try { System.IO.Directory.CreateSymbolicLink(link, outside.Root); directoryLinked = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        if (directoryLinked)
        {
            try { RejectCorrupt(() => new ContactContextStore(link)); }
            finally { System.IO.Directory.Delete(link); }
        }
        if (!linked || !directoryLinked) Console.WriteLine("SKIP contact context: unavailable OS symbolic-link privilege limits path checks");
        return Task.CompletedTask;
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException) { return; }
        throw new Exception("invalid contact context operation was accepted");
    }

    private static void RejectCorrupt(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("corrupt contact context was accepted");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Folder : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "audio-bridge-contact-check-" + Guid.NewGuid().ToString("N"));
        public DateTimeOffset Now { get; set; } = InitialNow;
        public string Directory => System.IO.Path.Combine(Root, "Results", "local", "contact-memory");
        public string Private(long userId) => System.IO.Path.Combine(Directory, "private-" + userId + ".json");
        public string Public(long userId) => System.IO.Path.Combine(Directory, "public-" + userId + ".json");
        public Folder() => System.IO.Directory.CreateDirectory(Root);
        public ContactContextStore Store() => new(Root, () => Now);
        public void Dispose() { if (System.IO.Directory.Exists(Root)) System.IO.Directory.Delete(Root, recursive: true); }
    }
}
