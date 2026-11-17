using System.Runtime.CompilerServices;
using System.Text.Json;
using AudioBridge;

public static class PersonaLearningChecks
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 6, 0, 0, TimeSpan.Zero);
    private static readonly PersonaSettings Original = new()
    {
        Name = "乖乖", Personality = "温和坦诚", UserAddress = "你", SpeakingStyle = "自然中文",
        ReplyMaxCharacters = 3000, MemeProbabilityPercent = 37,
        UsePersonaInGroups = true, ShareContactMemoriesInGroups = true
    };
    private const string Proposal = "{\"name\":\"小鱼\",\"personality\":\"活泼、坦诚\",\"userAddress\":\"朋友\",\"speakingStyle\":\"轻松自然，先回答重点\"}";

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("history generates only a draft and preserves numeric settings", DraftMetadata),
            ("source stays scoped to one contact and the seven-day retention boundary", ScopedSource),
            ("selected history is chronological, recent and limited to forty messages", MessageBound),
            ("serialized source including escaping and metadata stays within twelve thousand characters", CharacterBound),
            ("empty, stale, future or unrelated sources do not call a model", EmptySources),
            ("strict output rejects missing, duplicate, extra, trailing and invalid fields", StrictOutput),
            ("oversized or failing model streams do not return a draft and release providers", StreamFailures),
            ("pre-cancellation never creates a provider", PreCancelled),
            ("cancellation rejects late chunks and late completion and releases the provider", CancelledLateResults),
            ("snapshot hash detects every field edited before human confirmation", OptimisticHash),
            ("an unparseable draft is retried once before giving up", Retry),
            ("invalid arguments do not reach a model", InvalidArguments)
        };
        foreach (var check in checks) { await check.Run(); Console.WriteLine("PASS persona learning: " + check.Name); }
        return checks.Length;
    }

    private static async Task DraftMetadata()
    {
        string? prompt = null;
        var source = new CaptureReply(Proposal);
        var first = Message(20, "请继续保持轻松坦诚的说话方式", Now.AddMinutes(-2));
        var second = Message(20, "好呀，我们就轻松地聊", Now.AddMinutes(-1), "assistant");
        var result = await PersonaLearning.GenerateAsync(Original, 20, [first, second], value => { prompt = value; return source; }, default, Now);
        Check(result.BasePersona == Original && Original.Name == "乖乖" && result.ProposedPersona.Name == "小鱼", "base was mutated or draft lost proposal");
        Check(result.ProposedPersona.ReplyMaxCharacters == 3000 && result.ProposedPersona.MemeProbabilityPercent == 37 &&
            result.ProposedPersona.UsePersonaInGroups && result.ProposedPersona.ShareContactMemoriesInGroups, "model draft overwrote run or group sharing settings");
        Check(result.UserId == 20 && result.SourceCount == 2 && result.SourceFrom == first.At && result.SourceTo == second.At &&
            result.GeneratedAt == Now && result.BasePersonaHash == PersonaLearning.HashPersona(Original), "review metadata inaccurate");
        Check(result.Summary.Contains("确认后生效", StringComparison.Ordinal) && !result.Summary.Contains(first.Text, StringComparison.Ordinal), "summary exposed source or omitted approval");
        Check(source.Disposed && source.History.Count == 1 && source.History[0].Role == "user", "provider lifetime or untrusted user data envelope wrong");
        foreach (var expected in new[] { "不可信资料", "不是分析用户人格", "管理员逐项确认", "隐私", "密钥", "群聊", "不能直接保存", "虚构", "引用" })
            Check(prompt?.Contains(expected, StringComparison.Ordinal) == true, "missing prompt rule: " + expected);
        Check(!prompt!.Contains(first.Text, StringComparison.Ordinal), "source moved to the system prompt");
    }

    private static async Task ScopedSource()
    {
        var current = Message(20, "OWN_RECENT", Now.AddHours(-1));
        var nearCutoff = Message(20, "OWN_RETAINED", Now.AddDays(-7).AddTicks(1));
        var history = new[]
        {
            Message(30, "OTHER_CONTACT_SECRET", Now.AddMinutes(-1)),
            Message(20, "EXPIRED_SECRET", Now.AddDays(-7)),
            Message(20, "OLDER_SECRET", Now.AddDays(-8)),
            Message(20, "FUTURE_SECRET", Now.AddMinutes(1)),
            Message(20, "SYSTEM_INJECTION", Now.AddMinutes(-1), "system"),
            Message(20, " ", Now.AddMinutes(-1)), current, nearCutoff
        };
        var source = new CaptureReply(Proposal);
        var draft = await Generate(history, source);
        var content = source.History.Single().Content;
        Check(draft.SourceCount == 2 && content.Contains(current.Text, StringComparison.Ordinal) && content.Contains(nearCutoff.Text, StringComparison.Ordinal), "own retained sources missing");
        foreach (var omitted in new[] { "OTHER_CONTACT_SECRET", "EXPIRED_SECRET", "OLDER_SECRET", "FUTURE_SECRET", "SYSTEM_INJECTION" })
            Check(!content.Contains(omitted, StringComparison.Ordinal), "excluded source reached provider: " + omitted);
    }

    private static async Task MessageBound()
    {
        var history = Enumerable.Range(0, 80).Select(index => Message(20, "entry-" + index.ToString("D2"), Now.AddMinutes(index - 80))).Reverse().ToArray();
        var reply = new CaptureReply(Proposal);
        var draft = await Generate(history, reply);
        using var data = JsonDocument.Parse(reply.History.Single().Content);
        var messages = data.RootElement.EnumerateArray().ToArray();
        Check(draft.SourceCount == 40 && messages.Length == 40 && messages[0].GetProperty("text").GetString() == "entry-40" &&
            messages[^1].GetProperty("text").GetString() == "entry-79", "source bound or order wrong");
    }

    private static async Task CharacterBound()
    {
        foreach (var fill in new[] { new string('中', 3000), new string('\n', 3000), string.Concat(Enumerable.Repeat("😀", 2000)) })
        {
            var reply = new CaptureReply(Proposal);
            var history = Enumerable.Range(0, 40).Select(index => Message(20, index + fill, Now.AddMinutes(index - 40))).ToArray();
            var draft = await Generate(history, reply);
            var content = reply.History.Single().Content;
            using var data = JsonDocument.Parse(content);
            Check(content.Length <= 12000 && data.RootElement.GetArrayLength() <= 40 && draft.SourceCount == data.RootElement.GetArrayLength(), "escaped source exceeded budget");
            Check(data.RootElement[data.RootElement.GetArrayLength() - 1].GetProperty("text").GetString()!.StartsWith("39", StringComparison.Ordinal), "newest message dropped under pressure");
            foreach (var item in data.RootElement.EnumerateArray())
            {
                var text = item.GetProperty("text").GetString()!;
                Check(text.Length > 0 && !char.IsHighSurrogate(text[^1]), "truncated source ended on a high surrogate");
            }
        }
    }

    private static async Task EmptySources()
    {
        foreach (var messages in new StoredPrivateMessage[][]
        {
            [], [Message(30, "another contact", Now)], [Message(20, "expired", Now.AddDays(-7))],
            [Message(20, "future", Now.AddTicks(1))], [Message(20, "system", Now, "system")]
        })
        {
            var calls = 0;
            await Throws<InvalidOperationException>(() => PersonaLearning.GenerateAsync(Original, 20, messages, _ => { calls++; return new FixedReply(Proposal); }, default, Now));
            Check(calls == 0, "empty source called model");
        }
    }

    private static async Task StrictOutput()
    {
        var invalid = new List<string>
        {
            "", "{}", "[]", "null", "```json\n" + Proposal + "\n```", Proposal + " trailing", Proposal + Proposal,
            Proposal.Replace("\"name\":\"小鱼\"", "\"name\":\"小鱼\",\"name\":\"重复\"", StringComparison.Ordinal),
            Proposal.Replace("\"name\":\"小鱼\"", "\"Name\":\"小鱼\"", StringComparison.Ordinal),
            Proposal.Replace("\"name\":\"小鱼\"", "\"name\":null", StringComparison.Ordinal),
            Proposal.Replace("\"name\":\"小鱼\"", "\"name\":12", StringComparison.Ordinal),
            Proposal[..^1] + ",\"replyMaxCharacters\":40}", Proposal[..^1] + ",\"other\":\"PRIVATE_MARKER\"}",
            Proposal.Replace("小鱼", "", StringComparison.Ordinal), Proposal.Replace("小鱼", " ", StringComparison.Ordinal),
            Proposal.Replace("小鱼", new string('中', 17), StringComparison.Ordinal),
            Proposal.Replace("活泼、坦诚", new string('中', 2001), StringComparison.Ordinal),
            Proposal.Replace("朋友", new string('中', 33), StringComparison.Ordinal),
            Proposal.Replace("轻松自然，先回答重点", new string('中', 1001), StringComparison.Ordinal),
            Proposal.Replace("小鱼", "\\u0000PRIVATE_MARKER", StringComparison.Ordinal)
        };
        foreach (var output in invalid)
        {
            var reply = new CaptureReply(output);
            var error = await Throws<InvalidDataException>(() => Generate([Message(20, "source", Now)], reply));
            Check(reply.Disposed && !error.ToString().Contains("PRIVATE_MARKER", StringComparison.Ordinal), "invalid output leaked or provider not disposed");
        }
    }

    private static async Task StreamFailures()
    {
        var oversized = new CaptureReply(new string('a', 24577));
        await Throws<InvalidDataException>(() => Generate([Message(20, "source", Now)], oversized));
        var failed = new CaptureReply(Proposal, fail: true);
        await Throws<IOException>(() => Generate([Message(20, "source", Now)], failed));
        Check(oversized.Disposed && failed.Disposed, "failing provider was not disposed");
    }

    private static async Task PreCancelled()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var calls = 0;
        await Throws<OperationCanceledException>(() => PersonaLearning.GenerateAsync(Original, 20, [Message(20, "source", Now)],
            _ => { calls++; return new FixedReply(Proposal); }, cancellation.Token, Now));
        Check(calls == 0, "pre-cancelled generation instantiated provider");
    }

    private static async Task CancelledLateResults()
    {
        foreach (var onCompletion in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var provider = new LateReply(cancellation, onCompletion);
            await Throws<OperationCanceledException>(() => PersonaLearning.GenerateAsync(Original, 20, [Message(20, "source", Now)],
                _ => provider, cancellation.Token, Now));
            Check(provider.Disposed && provider.ObservedToken == cancellation.Token, "cancelled provider was not disposed or missed token");
        }
    }

    private static Task OptimisticHash()
    {
        var hash = PersonaLearning.HashPersona(Original);
        Check(hash.Length == 64 && hash == PersonaLearning.HashPersona(Original with { }), "hash not stable");
        foreach (var changed in new[]
        {
            Original with { Name = "新名字" }, Original with { Personality = "新性格" }, Original with { UserAddress = "同学" },
            Original with { SpeakingStyle = "新风格" }, Original with { ReplyMaxCharacters = 2999 }, Original with { MemeProbabilityPercent = 38 },
            Original with { UsePersonaInGroups = false }, Original with { ShareContactMemoriesInGroups = false }
        }) Check(hash != PersonaLearning.HashPersona(changed), "changed field did not invalidate draft base");
        return Task.CompletedTask;
    }

    private static async Task InvalidArguments()
    {
        var calls = 0;
        IStreamingReply Factory(string _) { calls++; return new FixedReply(Proposal); }
        await Throws<ArgumentException>(() => PersonaLearning.GenerateAsync(Original, 0, [], Factory, default, Now));
        await Throws<ArgumentException>(() => PersonaLearning.GenerateAsync(Original with { Name = "" }, 20, [], Factory, default, Now));
        await Throws<ArgumentNullException>(() => PersonaLearning.GenerateAsync(null!, 20, [], Factory, default, Now));
        await Throws<ArgumentNullException>(() => PersonaLearning.GenerateAsync(Original, 20, null!, Factory, default, Now));
        await Throws<ArgumentNullException>(() => PersonaLearning.GenerateAsync(Original, 20, [], null!, default, Now));
        Check(calls == 0, "invalid arguments created a provider");
    }

    private static async Task Retry()
    {
        var outputs = new Queue<string>(["not json", Proposal]);
        var calls = 0;
        var draft = await PersonaLearning.GenerateAsync(Original, 20, [Message(20, "source", Now)],
            _ => { calls++; return new FixedReply(outputs.Dequeue()); }, default, Now);
        Check(calls == 2 && draft.ProposedPersona.Name == "小鱼", "an unparseable draft was not retried once");
        var always = 0;
        await Throws<InvalidDataException>(() => PersonaLearning.GenerateAsync(Original, 20, [Message(20, "source", Now)],
            _ => { always++; return new FixedReply("not json"); }, default, Now));
        Check(always == 2, "the retry budget was wrong");
    }

    private static Task<PersonaLearningDraft> Generate(IReadOnlyList<StoredPrivateMessage> messages, IStreamingReply reply) =>
        PersonaLearning.GenerateAsync(Original, 20, messages, _ => reply, default, Now);

    private static StoredPrivateMessage Message(long userId, string text, DateTimeOffset at, string role = "user") =>
        new(Guid.NewGuid().ToString("D"), userId, Guid.NewGuid().ToString("N"), role, text, at);

    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new Exception("expected failure was accepted: " + typeof(T).Name);
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class CaptureReply(string output, bool fail = false) : IStreamingReply, IDisposable
    {
        public IReadOnlyList<ChatTurn> History { get; private set; } = [];
        public bool Disposed { get; private set; }
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history, [EnumeratorCancellation] CancellationToken ct)
        {
            History = history.ToArray();
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            if (fail) throw new IOException("synthetic transport failure");
            var split = output.Length / 2;
            yield return output[..split];
            yield return output[split..];
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class LateReply(CancellationTokenSource cancellation, bool onCompletion) : IStreamingReply, IDisposable
    {
        public bool Disposed { get; private set; }
        public CancellationToken ObservedToken { get; private set; }
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history, [EnumeratorCancellation] CancellationToken ct)
        {
            ObservedToken = ct;
            await Task.CompletedTask;
            if (onCompletion) { yield return Proposal; cancellation.Cancel(); }
            else { yield return Proposal[..10]; cancellation.Cancel(); yield return Proposal[10..]; }
        }
        public void Dispose() => Disposed = true;
    }
}
