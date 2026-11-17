using System.Text.Json;
using AudioBridge;

/// <summary>机器人自己整理长期笔记：严格解析、去重、每日限额、游标与失败不回退。</summary>
public static class MemoryLearnChecks
{
    private const long User = 29070481;

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("only the exact memories JSON is accepted", ParseRules),
            ("learned notes use new private messages and mark themselves as auto", Learns),
            ("existing notes are never duplicated", Dedupe),
            ("per-run and per-day limits bound automatic notes", Limits),
            ("the cursor survives restarts and skips already read messages", Cursor),
            ("a failed model round writes nothing and does not skip messages", Failure),
            ("damaged state and profile origins are handled strictly", Storage)
        };
        foreach (var check in checks)
        {
            await check.Run().WaitAsync(TimeSpan.FromSeconds(30));
            Console.WriteLine("PASS memory learn: " + check.Name);
        }
        return checks.Length;
    }

    private static Task ParseRules()
    {
        Check(MemoryLearner.Parse("""{"memories":[]}""").Count == 0, "empty list was rejected");
        var one = MemoryLearner.Parse("""{"memories":[{"text":"对方喜欢简短回答"}]}""");
        Check(one.Count == 1 && one[0] == "对方喜欢简短回答", "single note was not parsed");
        var twice = MemoryLearner.Parse("""{"memories":[{"text":"对方喜欢简短回答"},{"text":"对方喜欢 简短回答"}]}""");
        Check(twice.Count == 1, "whitespace duplicate was not collapsed");
        Reject("""{"memories":[],"extra":1}""", "extra root field was accepted");
        Reject("""{"memories":[{"text":"对方喜欢简短回答","id":"x"}]}""", "extra item field was accepted");
        Reject("""{"memories":[{"text":"短"}]}""", "too short note was accepted");
        Reject("""{"memories":[{"text":"对方喜欢简短回答\n第二行"}]}""", "newline note was accepted");
        Reject(JsonSerializer.Serialize(new { memories = new[] { new { text = new string('长', MemoryLearner.MaximumNoteCharacters + 1) } } }),
            "over long note was accepted");
        Reject("""{"memories":[{"text":"对方喜欢简短回答"},{"text":"对方在准备考试"},{"text":"对方喜欢猫"}]}""", "more than two notes were accepted");
        Reject("""[{"text":"对方喜欢简短回答"}]""", "array root was accepted");
        Reject("""{"memories":[],"memories":[]}""", "duplicate root keys were accepted");
        Reject("""not json""", "garbage was accepted");
        return Task.CompletedTask;
    }

    private static async Task Learns()
    {
        using var fixture = new Fixture();
        fixture.Say("我叫示熠，平时喜欢简短回答，别写太长。", "1");
        fixture.Say("好的，我改天再问你。", "2", assistant: true);
        var brain = new FakeBrain(_ => """{"memories":[{"text":"示熠希望回答简短，不喜欢长段落"}]}""");
        var result = await fixture.Learner(brain).RunOnceAsync([User], CancellationToken.None);
        Check(result.Added == 1 && result.Contacts == 1 && result.Error is null, "one note was not added");
        var memories = fixture.Profiles.ListMemories(User);
        Check(memories.Count == 1 && memories[0].Origin == "auto", "note is not marked as auto");
        var prompt = fixture.Prompts.Single();
        Check(prompt.Contains("我叫示熠", StringComparison.Ordinal) && prompt.Contains("不要记录", StringComparison.Ordinal) &&
            prompt.Contains("我: 好的，我改天再问你。", StringComparison.Ordinal), "prompt missed the transcript or the rules");
    }

    private static async Task Dedupe()
    {
        using var fixture = new Fixture();
        fixture.Profiles.AddMemory(User, "示熠希望回答简短");
        fixture.Say("记住我说话要短一点。", "1");
        var brain = new FakeBrain(_ => """{"memories":[{"text":"示熠希望回答 简短"},{"text":"示熠在准备考试"}]}""");
        var result = await fixture.Learner(brain).RunOnceAsync([User], CancellationToken.None);
        Check(result.Added == 1, "an existing note was duplicated");
        Check(fixture.Profiles.ListMemories(User).Count == 2, "duplicate write changed the note count");
    }

    private static async Task Limits()
    {
        using var fixture = new Fixture();
        var brain = new FakeBrain(call => JsonSerializer.Serialize(new
        {
            memories = new[] { new { text = "示熠的第" + call + "条偏好" }, new { text = "示熠的第" + call + "条习惯" } }
        }));
        var learner = fixture.Learner(brain, maximumPerRun: 2, maximumPerDay: 4);
        var added = 0;
        for (var round = 0; round < 4; round++)
        {
            fixture.Say("这是第" + round + "句新消息，随便说点什么。", "m" + round);
            added += (await learner.RunOnceAsync([User], CancellationToken.None)).Added;
        }
        Check(added == 4, "daily limit was not enforced: " + added);
        Check(fixture.Profiles.ListMemories(User).Count == 4, "daily limit changed the stored count");
        Check(fixture.Prompts.Count == 2, "the model was still called after the daily limit");
        fixture.Say("再补一句新消息。", "m9");
        Check((await learner.RunOnceAsync([User], CancellationToken.None)).Added == 0, "daily limit kept writing");
    }

    private static async Task Cursor()
    {
        using var fixture = new Fixture();
        fixture.Say("第一句：我在写论文。", "1");
        var brain = new FakeBrain(_ => """{"memories":[]}""");
        await fixture.Learner(brain).RunOnceAsync([User], CancellationToken.None);
        Check(fixture.Prompts.Count == 1, "first round did not call the model");
        await fixture.Learner(brain).RunOnceAsync([User], CancellationToken.None);
        Check(fixture.Prompts.Count == 1, "no new message still called the model");
        var reopened = new MemoryLearnStore(fixture.StorePath);
        Check(reopened.Get(User).LastMessageId == "1", "cursor did not persist");
        fixture.Say("第二句：明天要交报告。", "2");
        var learner = new MemoryLearner(fixture.Profiles, fixture.Contacts, reopened,
            prompt => { fixture.Prompts.Add(prompt); return brain; }, live: true, (_, _) => { });
        await learner.RunOnceAsync([User], CancellationToken.None);
        Check(fixture.Prompts.Count == 2 && fixture.Prompts[1].Contains("明天要交报告", StringComparison.Ordinal) &&
            !fixture.Prompts[1].Contains("第一句", StringComparison.Ordinal), "only the unread message should reach the model");
    }

    private static async Task Failure()
    {
        using var fixture = new Fixture();
        fixture.Say("我在准备考研。", "1");
        var broken = new FakeBrain(_ => throw new IOException("模型不可用"));
        var failed = await fixture.Learner(broken).RunOnceAsync([User], CancellationToken.None);
        Check(failed.Error is not null && fixture.Profiles.ListMemories(User).Count == 0, "a failed round wrote notes");
        Check(fixture.Store.Get(User).LastMessageId is null, "a failed round advanced the cursor");
        var healthy = new FakeBrain(_ => """{"memories":[{"text":"示熠在准备考研"}]}""");
        var result = await fixture.Learner(healthy).RunOnceAsync([User], CancellationToken.None);
        Check(result.Added == 1 && fixture.Prompts[^1].Contains("我在准备考研", StringComparison.Ordinal),
            "the same messages were not retried after the failure");
    }

    private static Task Storage()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.StorePath)!);
        File.WriteAllText(fixture.StorePath, "{broken");
        Check(fixture.Store.Get(User).LastMessageId is null, "damaged state was trusted");
        fixture.Store.Save(User, new("42", "2026-10-26", 1));
        Check(new MemoryLearnStore(fixture.StorePath).Get(User).LearnedToday == 1, "state did not survive a reload");
        fixture.Profiles.AddMemory(User, "人工写的笔记");
        fixture.Profiles.AddMemory(User, "自己整理的笔记", "auto");
        Check(fixture.Profiles.ListMemories(User).Count(entry => entry.Origin == "auto") == 1, "auto origin did not persist");
        var profilePath = Path.Combine(fixture.Root, "bot-profile.local.json");
        var json = File.ReadAllText(profilePath).Replace("\"origin\": \"auto\"", "\"origin\": \"bogus\"", StringComparison.Ordinal);
        File.WriteAllText(profilePath, json);
        try { _ = new BotProfileStore(fixture.Root).ListMemories(User); }
        catch (InvalidDataException) { return Task.CompletedTask; }
        throw new Exception("unknown origin was accepted");
    }

    private static void Reject(string json, string reason)
    {
        try { _ = MemoryLearner.Parse(json); }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException) { return; }
        throw new Exception(reason);
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }

    private sealed class FakeBrain(Func<int, string> response) : IStreamingReply
    {
        private int calls;
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var call = ++calls;
            await Task.CompletedTask;
            yield return response(call);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "memory-learn-" + Guid.NewGuid().ToString("N"));
        public BotProfileStore Profiles { get; }
        public ContactContextStore Contacts { get; }
        public MemoryLearnStore Store { get; }
        public List<string> Prompts { get; } = [];
        public string StorePath => Path.Combine(Root, "Results", "local", "memory-learn.json");

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "Results", "local"));
            Profiles = new BotProfileStore(Root);
            Contacts = new ContactContextStore(Root);
            Store = new MemoryLearnStore(StorePath);
        }

        public void Say(string text, string messageId, bool assistant = false) =>
            Contacts.AppendMessage(User, assistant ? "assistant" : "user", text, messageId);

        public MemoryLearner Learner(IStreamingReply brain, int maximumPerRun = 2, int maximumPerDay = 6) =>
            new(Profiles, Contacts, Store, prompt => { Prompts.Add(prompt); return brain; }, live: true, (_, _) => { },
                maximumPerRun: maximumPerRun, maximumPerDay: maximumPerDay);

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch (IOException) { }
        }
    }
}
