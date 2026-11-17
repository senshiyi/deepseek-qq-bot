using System.Text.Json;

namespace AudioBridge;

/// <summary>
/// 好友申请自动同意：只认 post_type=request 且 request_type=friend 的事件；一小时最多3个、24小时最多20个，
/// 同一时间只处理一个；暂停、预览或未登录时不处理。申请不进入聊天队列，也不改动私聊白名单。
/// </summary>
public sealed class FriendRequestAcceptor
{
    public const int PerHourLimit = 3;
    public const int PerDayLimit = 20;
    private const int MaximumFlagCharacters = 256;
    private static readonly TimeSpan HourWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan DayWindow = TimeSpan.FromHours(24);

    private readonly Func<string, CancellationToken, Task> approve;
    private readonly Func<bool> paused;
    private readonly long selfId;
    private readonly Action<string, object> log;
    private readonly Func<DateTimeOffset> clock;
    private readonly CancellationToken stopping;
    private readonly bool live;
    private readonly object gate = new();
    private readonly List<DateTimeOffset> handled = [];
    private int inFlight;

    public FriendRequestAcceptor(Func<string, CancellationToken, Task> approve, Func<bool> paused, long selfId,
        Action<string, object> log, bool live = true, Func<DateTimeOffset>? clock = null, CancellationToken stopping = default)
    {
        this.approve = approve ?? throw new ArgumentNullException(nameof(approve));
        this.paused = paused ?? throw new ArgumentNullException(nameof(paused));
        this.selfId = selfId > 0 ? selfId : throw new ArgumentOutOfRangeException(nameof(selfId));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.live = live;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.stopping = stopping;
    }

    /// <summary>处理一个原始事件：true 表示它确实是好友申请（无论同意与否），false 表示不归这里管。</summary>
    public bool TryHandle(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !string.Equals(Text(root, "post_type"), "request", StringComparison.Ordinal) ||
            !string.Equals(Text(root, "request_type"), "friend", StringComparison.Ordinal)) return false;
        if (!TryId(root, "user_id", out var userId) || !TryId(root, "self_id", out var eventSelf) || eventSelf != selfId)
        { Skip(userId, "invalid_event"); return true; }
        var flag = Text(root, "flag");
        if (flag.Length is < 1 or > MaximumFlagCharacters || flag.Any(char.IsControl))
        { Skip(userId, "invalid_flag"); return true; }
        if (!live) { Skip(userId, "preview"); return true; }
        lock (gate)
        {
            if (paused()) { Skip(userId, "paused"); return true; }
            if (inFlight > 0) { Skip(userId, "busy"); return true; }
            var now = clock();
            handled.RemoveAll(stamp => now - stamp >= DayWindow);
            if (handled.Count(stamp => now - stamp < HourWindow) >= PerHourLimit || handled.Count >= PerDayLimit)
            { Skip(userId, "rate_limited"); return true; }
            // 先记额度再发请求：失败也算一次，避免反复重放。
            handled.Add(now);
            inFlight = 1;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await approve(flag, stopping).ConfigureAwait(false);
                log("friend_request", new { userId, approved = true });
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception error) when (error is OneBotMessageException or IOException or HttpRequestException or OperationCanceledException)
            { log("friend_request_failed", new { userId, type = error.GetType().Name }); }
            finally { lock (gate) inFlight = 0; }
        }, stopping);
        return true;
    }

    private void Skip(long userId, string reason) => log("friend_request_ignored", new { userId, reason });

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool TryId(JsonElement root, string name, out long id)
    {
        id = 0;
        if (!root.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt64(out id) && id > 0;
        return value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0;
    }
}
