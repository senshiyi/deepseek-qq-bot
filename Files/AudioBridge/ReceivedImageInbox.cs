using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AudioBridge;

public sealed record ReceivedImageCandidate(string Id, long SenderId, long GroupId, long MessageId,
    DateTimeOffset At, bool IsPrivate);

internal sealed record ReceivedImageSelection(ReceivedImageCandidate Candidate, Uri Url);

/// <summary>
/// Short-lived metadata for manual meme import. Observe is called only after GroupChatPolicy.TryParse
/// admits the event to its current group/contact allowlist. No image is downloaded or persisted here.
/// </summary>
public sealed class ReceivedImageInbox
{
    public const int MaximumItems = 64;
    // ponytail: 只在内存里保留最近一天的候选（图片链接由 QQ 提供，过期需重发）；要永久画廊再加落盘。
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly object gate = new();
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, ReceivedImageSelection> selections = new(StringComparer.Ordinal);
    private readonly Dictionary<(bool IsPrivate, long Id, long MessageId), EventRecord> events = [];
    private long nextOrder;
    public long ExpectedSelfId { get; }

    public ReceivedImageInbox(long expectedSelfId, TimeProvider? timeProvider = null)
    {
        if (expectedSelfId <= 0) throw new ArgumentException("请绑定明确的机器人账号。");
        ExpectedSelfId = expectedSelfId;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool Observe(JsonElement raw, ParsedGroupMessage authorized)
    {
        ArgumentNullException.ThrowIfNull(authorized);
        if (raw.ValueKind != JsonValueKind.Object || !authorized.ImageSeen || authorized.SenderId <= 0 ||
            authorized.SenderId == ExpectedSelfId || authorized.IsPrivate && authorized.GroupId != 0 ||
            !authorized.IsPrivate && authorized.GroupId <= 0) return false;
        var text = raw.GetRawText();
        if (text.Length > GroupChatPolicy.MaximumEventBytes || Encoding.UTF8.GetByteCount(text) > GroupChatPolicy.MaximumEventBytes ||
            !Is(raw, "post_type", "message") || !Is(raw, "message_type", authorized.IsPrivate ? "private" : "group") ||
            Is(raw, "sub_type", "self_sent") || Is(raw, "sub_type", "notice") ||
            !Id(raw, "self_id", out var self) || self != ExpectedSelfId ||
            !Id(raw, "user_id", out var sender) || sender != authorized.SenderId ||
            !Id(raw, "message_id", out var message, signed: true) || message != authorized.MessageId ||
            !raw.TryGetProperty("sender", out var senderObject) || !Id(senderObject, "user_id", out var senderCheck) || senderCheck != sender ||
            !authorized.IsPrivate && (!Id(raw, "group_id", out var group) || group != authorized.GroupId) ||
            !raw.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out var seconds)) return false;
        DateTimeOffset at;
        try { at = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return false; }
        var now = timeProvider.GetUtcNow();
        if (at != authorized.At || now - at > GroupChatPolicy.MaximumEventAge || at - now > GroupChatPolicy.MaximumFutureSkew ||
            !raw.TryGetProperty("message", out var segments) || segments.ValueKind != JsonValueKind.Array ||
            segments.GetArrayLength() > GroupChatPolicy.MaximumSegments) return false;
        var urls = new List<Uri>();
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object || !Is(segment, "type", "image")) continue;
            if (!segment.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("url", out var value) || value.ValueKind != JsonValueKind.String ||
                !QqEmojiClient.TryImageUri(value.GetString(), out var uri)) return false;
            if (!urls.Contains(uri)) urls.Add(uri);
            if (urls.Count > MaximumItems) return false;
        }
        if (urls.Count == 0) return false;
        var key = (authorized.IsPrivate, authorized.IsPrivate ? sender : authorized.GroupId, message);
        lock (gate)
        {
            Prune(now);
            if (events.TryGetValue(key, out var previous))
            {
                if (previous.At != at || previous.SenderId != sender || !previous.Urls.ToHashSet().SetEquals(urls))
                {
                    RemoveSelections(previous);
                    events[key] = previous with { Ids = [] }; // A conflicting replay can never replace the first selection.
                }
                return false;
            }
            while (events.Count >= MaximumItems || selections.Count + urls.Count > MaximumItems)
            {
                var oldest = events.MinBy(item => (item.Value.At, item.Value.Order));
                RemoveSelections(oldest.Value); events.Remove(oldest.Key);
            }
            var ids = new List<string>();
            foreach (var uri in urls)
            {
                var id = Guid.NewGuid().ToString("N");
                var candidate = new ReceivedImageCandidate(id, sender, authorized.GroupId, message, at, authorized.IsPrivate);
                selections.Add(id, new(candidate, uri)); ids.Add(id);
            }
            events.Add(key, new(at, sender, urls.ToArray(), ids.ToArray(), nextOrder++));
            return true;
        }
    }

    public IReadOnlyList<ReceivedImageCandidate> List()
    {
        lock (gate)
        {
            Prune(timeProvider.GetUtcNow());
            return selections.Values.Select(item => item.Candidate).OrderByDescending(item => item.At).ToArray();
        }
    }

    public void Clear() { lock (gate) { selections.Clear(); events.Clear(); nextOrder = 0; } }

    internal ReceivedImageSelection Resolve(string id)
    {
        lock (gate)
        {
            Prune(timeProvider.GetUtcNow());
            if (id is null || !selections.TryGetValue(id, out var selection)) throw Missing();
            return selection;
        }
    }

    internal void EnsureCurrent(ReceivedImageSelection selection)
    {
        lock (gate)
        {
            Prune(timeProvider.GetUtcNow());
            if (!selections.TryGetValue(selection.Candidate.Id, out var current) || !ReferenceEquals(current, selection)) throw Missing();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var item in events.Where(item => now - item.Value.At >= Lifetime || item.Value.At - now > GroupChatPolicy.MaximumFutureSkew).ToArray())
        { RemoveSelections(item.Value); events.Remove(item.Key); }
    }
    private void RemoveSelections(EventRecord item) { foreach (var id in item.Ids) selections.Remove(id); }
    private static ArgumentException Missing() => new("收到的图片已过期或不再可用，请重新发送并刷新列表。");
    private static bool Is(JsonElement root, string key, string expected) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == expected;
    private static bool Id(JsonElement root, string key, out long id, bool signed = false)
    {
        id = 0;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var value)) return false;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
        return long.TryParse(text, signed ? NumberStyles.AllowLeadingSign : NumberStyles.None, CultureInfo.InvariantCulture, out id) && (signed || id > 0);
    }
    private sealed record EventRecord(DateTimeOffset At, long SenderId, Uri[] Urls, string[] Ids, long Order);
}
