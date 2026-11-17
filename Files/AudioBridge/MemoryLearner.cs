using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AudioBridge;

public sealed record MemoryLearnResult(int Contacts, int Added, Exception? Error);

/// <summary>
/// 机器人自己在私聊后整理长期笔记：每天限额、只看没处理过的消息、模型失败不推进进度。
/// 只写"稳定信息"，一次性的状态和敏感信息一律不进；人工在面板补充或修正。
/// </summary>
public sealed class MemoryLearner
{
    public const int MaximumNoteCharacters = 200;
    public const int MinimumNoteCharacters = 4;
    public const int MaximumHistoryMessages = 40;
    private const int MaximumPromptCharacters = 12_000;
    private const int MaximumOutputCharacters = 8_000;
    private const int SoftMemoryLimit = 30;
    private readonly BotProfileStore profiles;
    private readonly ContactContextStore contacts;
    private readonly MemoryLearnStore store;
    private readonly Func<string, IStreamingReply> replyFactory;
    private readonly bool live;
    private readonly Action<string, object> log;
    private readonly TimeProvider time;
    private readonly int maximumPerRun;
    private readonly int maximumPerDay;

    public MemoryLearner(BotProfileStore profiles, ContactContextStore contacts, MemoryLearnStore store,
        Func<string, IStreamingReply> replyFactory, bool live, Action<string, object> log,
        TimeProvider? time = null, int maximumPerRun = 2, int maximumPerDay = 6)
    {
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.replyFactory = replyFactory ?? throw new ArgumentNullException(nameof(replyFactory));
        this.live = live;
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.time = time ?? TimeProvider.System;
        if (maximumPerRun is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(maximumPerRun), "每次最多整理1～5条。");
        if (maximumPerDay is < 1 or > 20 || maximumPerDay < maximumPerRun)
            throw new ArgumentOutOfRangeException(nameof(maximumPerDay), "每天最多整理1～20条，且不少于每次上限。");
        this.maximumPerRun = maximumPerRun;
        this.maximumPerDay = maximumPerDay;
    }

    public async Task<MemoryLearnResult> RunOnceAsync(IEnumerable<long> userIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (!live) return new(0, 0, null);
        var examined = 0;
        var added = 0;
        Exception? lastError = null;
        foreach (var user in userIds.Distinct().ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (user <= 0) continue;
            examined++;
            try { added += await LearnAsync(user, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidDataException or TimeoutException or HttpRequestException or InvalidOperationException or NotSupportedException)
            {
                lastError = error;
                log("memory_learn_failed", new { userId = user, type = error.GetType().Name, code = ReplyFailure.From(error).Code });
            }
        }
        return new(examined, added, lastError);
    }

    private async Task<int> LearnAsync(long user, CancellationToken ct)
    {
        var today = CompanionStore.ShanghaiTime(time.GetUtcNow()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var state = store.Get(user);
        if (state.Day != today) state = state with { Day = today, LearnedToday = 0 };
        if (state.LearnedToday >= maximumPerDay) return 0;
        var existing = profiles.ListMemories(user);
        if (existing.Count >= SoftMemoryLimit) return 0;
        var fresh = Fresh(contacts.Recent(user, 60), state.LastMessageId);
        if (fresh.Count == 0 || !fresh.Any(message => message.Role == "user")) return 0;
        var prompt = BuildPrompt(existing, fresh, Math.Min(maximumPerRun, maximumPerDay - state.LearnedToday));
        var candidates = Parse(await CollectAsync(replyFactory(prompt), ct).ConfigureAwait(false), maximumPerRun);
        var known = existing.Select(memory => Normalize(memory.Text)).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var candidate in candidates)
        {
            if (added >= maximumPerRun || state.LearnedToday + added >= maximumPerDay) break;
            if (!known.Add(Normalize(candidate))) continue;
            try { profiles.AddMemory(user, candidate, "auto"); added++; }
            catch (ArgumentException) { break; } // 到 32 条硬上限：停下，人工处理。
        }
        // 不论写了几条，都把游标推到这批消息的最后一条：同样的对话不会反复喂给模型。
        store.Save(user, new(fresh[^1].MessageId, state.Day, state.LearnedToday + added));
        log("memory_learned", new { userId = user, examined = fresh.Count, added, learnedToday = state.LearnedToday + added });
        return added;
    }

    /// <summary>游标之后的消息；游标已被七天清理时退回最近的那批。</summary>
    private static IReadOnlyList<StoredPrivateMessage> Fresh(IReadOnlyList<StoredPrivateMessage> history, string? cursor)
    {
        if (cursor is null) return Trim(history);
        var index = -1;
        for (var position = history.Count - 1; position >= 0; position--)
            if (string.Equals(history[position].MessageId, cursor, StringComparison.Ordinal)) { index = position; break; }
        if (index < 0) return Trim(history);
        var rest = history.Skip(index + 1).ToArray();
        return Trim(rest);
    }

    private static IReadOnlyList<StoredPrivateMessage> Trim(IReadOnlyList<StoredPrivateMessage> messages) =>
        messages.Count <= MaximumHistoryMessages ? messages : messages.Skip(messages.Count - MaximumHistoryMessages).ToArray();

    internal static string BuildPrompt(IReadOnlyList<ContactMemory> existing, IReadOnlyList<StoredPrivateMessage> fresh, int maximum)
    {
        var prompt = new StringBuilder();
        prompt.Append("你在替自己写长期笔记：只根据下面的对话整理以后仍然用得上的稳定信息。\n")
            .Append("可以记录：身份与称呼、明确的喜好与忌口、习惯、工作或学习安排、重要约定、关系变化。\n")
            .Append("不要记录：一次性的状态和情绪（困了、刚才不开心）、玩笑或反话、临时计划、密码令牌账号等敏感信息、你自己说的话，以及已有笔记里已经写过的内容。\n")
            .Append("标注（群聊）的是对方在群里说的，同样可以整理成笔记，但不要写成「他在私聊里说」。\n")
            .Append("没有值得记的就返回空数组。每条不超过 ").Append(MaximumNoteCharacters)
            .Append(" 字，写成简洁的陈述。\n只输出严格 JSON：{\"memories\":[{\"text\":\"…\"}]}，最多 ").Append(maximum).Append(" 条。\n");
        prompt.Append("【已有长期笔记】\n");
        if (existing.Count == 0) prompt.Append("（还没有）\n");
        foreach (var memory in existing.OrderByDescending(item => item.UpdatedAt).Take(20))
            prompt.Append("- ").Append(Limit(memory.Text, 200)).Append('\n');
        prompt.Append("【最近的对话】\n");
        var used = prompt.Length;
        var lines = new List<string>();
        for (var position = fresh.Count - 1; position >= 0; position--)
        {
            var message = fresh[position];
            var speaker = message.Role != "user" ? "我: "
                : ContactContextStore.ChannelOf(message) == "group" ? "对方（群聊）: " : "对方: ";
            var line = speaker + Limit(ContactContextStore.BodyOf(message), 400) + "\n";
            if (used + line.Length > MaximumPromptCharacters - 1000) break;
            lines.Add(line);
            used += line.Length;
        }
        for (var position = lines.Count - 1; position >= 0; position--) prompt.Append(lines[position]);
        if (lines.Count == 0) prompt.Append("（这段时间没有新的私聊内容）\n");
        return prompt.ToString();
    }

    private async Task<string> CollectAsync(IStreamingReply source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        var content = new StringBuilder();
        try
        {
            await foreach (var fragment in source.StreamAsync([new("user", "请按上面的要求整理长期笔记，只输出 JSON。")], ct).ConfigureAwait(false))
            {
                content.Append(fragment);
                if (content.Length > MaximumOutputCharacters) throw new InvalidDataException("记忆整理输出过长，已丢弃。");
            }
        }
        finally { if (source is IDisposable disposable) disposable.Dispose(); }
        return content.ToString();
    }

    /// <summary>严格解析：只接受 {"memories":[{"text":"…"}]}，多余字段、超长、重复一律拒绝。</summary>
    public static IReadOnlyList<string> Parse(string json, int maximum = 2)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (maximum is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(maximum));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 6 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count() ||
            root.EnumerateObject().Any(property => property.Name != "memories") ||
            !root.TryGetProperty("memories", out var list) || list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() > maximum)
            throw new InvalidDataException("记忆整理输出不是约定的JSON。");
        var result = new List<string>(list.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                item.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != item.EnumerateObject().Count() ||
                item.EnumerateObject().Any(property => property.Name != "text") ||
                !item.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("记忆整理输出不是约定的JSON。");
            var text = (value.GetString() ?? "").Trim();
            if (text.Length is < MinimumNoteCharacters or > MaximumNoteCharacters || text.Any(char.IsControl))
                throw new InvalidDataException("记忆整理输出了无效的笔记正文。");
            if (!seen.Add(Normalize(text))) continue;
            result.Add(text);
        }
        return result;
    }

    /// <summary>比较用的规范形：去掉所有空白并忽略英文大小写，中文换个空格不算新笔记。</summary>
    internal static string Normalize(string text) =>
        new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum].TrimEnd() + "…";
}

/// <summary>每位联系人"已经整理到哪条消息"和当天用量的本机状态。</summary>
public sealed class MemoryLearnStore
{
    public sealed record ContactState(string? LastMessageId, string Day, int LearnedToday);
    private const int MaximumBytes = 256 * 1024;
    private readonly object gate = new();
    private readonly string path;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public MemoryLearnStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public ContactState Get(long userId)
    {
        lock (gate) return Load().GetValueOrDefault(userId) ?? new(null, "", 0);
    }

    public void Save(long userId, ContactState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (userId <= 0) throw new ArgumentException("联系人QQ号必须是正整数。");
        lock (gate)
        {
            var document = Load();
            document[userId] = state;
            Persist(document);
        }
    }

    private Dictionary<long, ContactState> Load()
    {
        try
        {
            if (!File.Exists(path)) return [];
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumBytes) return [];
            var value = JsonSerializer.Deserialize<Dictionary<long, ContactState>>(File.ReadAllText(path), Json);
            return value is null ? [] : new Dictionary<long, ContactState>(value.Where(pair => pair.Key > 0 && pair.Value is not null));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // 状态坏了只影响这轮整理，不阻塞聊天；游标重建后会按去重继续。
            return [];
        }
    }

    private void Persist(Dictionary<long, ContactState> document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, Json));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }
}
