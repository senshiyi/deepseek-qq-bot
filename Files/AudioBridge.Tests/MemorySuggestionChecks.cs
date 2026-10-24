using AudioBridge;

public static class MemorySuggestionChecks
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 1, 0, 0, TimeSpan.Zero);

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("only explicit prefixed user requests become review candidates", ExplicitRequests),
            ("candidate source metadata and contact boundaries stay intact", ContactScope),
            ("duplicate and already saved notes are filtered per contact", Deduplication),
            ("candidate selection stays recent and bounded", Bounds),
            ("candidates follow existing seven-day history and do not save themselves", RetentionAndNoWrites),
            ("saving identical notes is idempotent at the full contact limit", IdempotentSave)
        };
        foreach (var check in checks)
        {
            await check.Run();
            Console.WriteLine("PASS memory suggestions: " + check.Name);
        }
        return checks.Length;
    }

    private static Task ExplicitRequests()
    {
        foreach (var prefix in new[] { "记住", "请记住", "帮我记住", "请帮我记住", "记一下", "请记一下", "记下来", "请记下来" })
            foreach (var separator in new[] { "：", ":" })
                Check(MemorySuggestions.ExtractExplicitRequest("  " + prefix + " " + separator + " 喜欢热茶  ") == "喜欢热茶", "explicit request was not recognized");
        foreach (var text in new[] { "喜欢热茶", "你记住了吗", "记住我喜欢热茶", "他说“记住：喜欢热茶”", "请忘记：喜欢热茶", "记住：  ", "记住：\0秘密", "记住：" + new string('中', 1001) })
            Check(MemorySuggestions.ExtractExplicitRequest(text) is null, "ordinary, quoted or invalid text became a candidate");
        Check(MemorySuggestions.ExtractExplicitRequest("记住：第一行\n第二行") == "第一行\n第二行", "valid multiline note was altered");
        var assistant = Message(20, "记住：助手机器人的自述") with { Role = "assistant" };
        Check(MemorySuggestions.ForContact(20, [assistant], []).Count == 0, "assistant claimed memory became user evidence");
        return Task.CompletedTask;
    }

    private static Task ContactScope()
    {
        var source = Message(20, "请记住：甲的私密偏好");
        var other = Message(30, "记住：乙的私密偏好");
        var result = MemorySuggestions.ForContact(20, [other, source], []);
        var candidate = result.Single();
        Check(candidate.UserId == source.UserId && candidate.SourceId == source.Id &&
              candidate.SourceMessageId == source.MessageId && candidate.At == source.At && candidate.Text == "甲的私密偏好",
            "candidate metadata or contact scope changed");
        Check(MemorySuggestions.ForContact(40, [source, other], []).Count == 0, "missing contact inherited another contact's request");
        return Task.CompletedTask;
    }

    private static Task Deduplication()
    {
        var old = Message(20, "记住：相同正文", -2);
        var latest = Message(20, "请记住： 相同正文 ", -1);
        var saved = Message(20, "记住：已经保存");
        var otherContactNote = new ContactMemory(Guid.NewGuid().ToString("D"), 30, "相同正文", Now, Now);
        var ownNote = new ContactMemory(Guid.NewGuid().ToString("D"), 20, "已经保存", Now, Now);
        var result = MemorySuggestions.ForContact(20, [old, latest, saved], [otherContactNote, ownNote]);
        Check(result.Single().SourceId == latest.Id, "duplicate source was not latest or other contact note suppressed candidate");
        Check(MemorySuggestions.ForContact(20, [latest], [otherContactNote, ownNote with { Text = "相同正文" }]).Count == 0,
            "saved private note still appeared as pending");
        return Task.CompletedTask;
    }

    private static Task Bounds()
    {
        var messages = Enumerable.Range(0, 40).Select(index => Message(20, "记住：条目" + index, index)).ToArray();
        var result = MemorySuggestions.ForContact(20, messages, []);
        Check(result.Count == 32 && result.First().Text == "条目39" && result.Last().Text == "条目8", "default candidate bound or order changed");
        Check(MemorySuggestions.ForContact(20, messages, [], 1).Single().Text == "条目39", "explicit maximum was ignored");
        Reject(() => MemorySuggestions.ForContact(0, [], []));
        Reject(() => MemorySuggestions.ForContact(20, [], [], 0));
        Reject(() => MemorySuggestions.ForContact(20, [], [], 33));
        return Task.CompletedTask;
    }

    private static Task RetentionAndNoWrites()
    {
        using var folder = new Folder();
        var now = Now;
        var contacts = new ContactContextStore(folder.Root, () => now);
        var profiles = new BotProfileStore(folder.Root);
        contacts.AppendMessage(20, "user", "记住：过期请求", "expired", Now.AddDays(-6));
        contacts.AppendMessage(20, "user", "记住：保留请求", "recent", Now);
        now = Now.AddDays(2);
        var retained = contacts.Recent(20, 1000);
        var paths = Directory.GetFiles(folder.Root, "*", SearchOption.AllDirectories);
        var bytes = paths.ToDictionary(path => path, File.ReadAllBytes);
        var result = MemorySuggestions.ForContact(20, retained, profiles.ListMemories(20));
        Check(result.Single().Text == "保留请求", "candidate bypassed transcript retention");
        Check(profiles.ListMemories(20).Count == 0 && profiles.BuildPrivateMemoryContext(20) == "", "candidate became long-term memory without approval");
        Check(paths.SequenceEqual(Directory.GetFiles(folder.Root, "*", SearchOption.AllDirectories)) &&
              paths.All(path => File.ReadAllBytes(path).SequenceEqual(bytes[path])), "candidate derivation wrote files");
        Check(contacts.BuildGroupPreferenceContext(20) == "", "private candidate became a group preference");
        contacts.DeleteHistory(20);
        Check(MemorySuggestions.ForContact(20, contacts.Recent(20, 1000), profiles.ListMemories(20)).Count == 0, "deleted raw source left a candidate");
        return Task.CompletedTask;
    }

    private static Task IdempotentSave()
    {
        using var folder = new Folder();
        var store = new BotProfileStore(folder.Root);
        var first = store.AddMemory(20, " 相同正文 ");
        for (var index = 1; index < 32; index++) store.AddMemory(20, "条目" + index);
        var path = Path.Combine(folder.Root, "bot-profile.local.json");
        var bytes = File.ReadAllBytes(path);
        var repeated = new BotProfileStore(folder.Root).AddMemory(20, "相同正文");
        Check(repeated == first && store.ListMemories(20).Count == 32, "full contact duplicated note or replaced metadata");
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "idempotent save rewrote the store");
        Reject(() => store.AddMemory(20, "第33条"));
        var independent = store.AddMemory(30, "相同正文");
        Check(independent.UserId == 30 && independent.Id != first.Id && store.ListMemories(30).Count == 1, "another contact's same note was merged");
        return Task.CompletedTask;
    }

    private static StoredPrivateMessage Message(long userId, string text, int seconds = 0) =>
        new(Guid.NewGuid().ToString("D"), userId, Guid.NewGuid().ToString("N"), "user", text, Now.AddSeconds(seconds));

    private static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("invalid request was accepted");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class Folder : IDisposable
    {
        private readonly string parent = Path.GetFullPath(Path.Combine("Cache", "memory-suggestion-checks"));
        public string Root { get; }
        public Folder()
        {
            Root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试目录不在预定范围内。");
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
