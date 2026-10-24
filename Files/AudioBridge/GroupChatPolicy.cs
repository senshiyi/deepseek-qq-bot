using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AudioBridge;

public enum GroupChatMode { Mention, Active, Manual }
public enum GroupChatTrigger { Unspecified, Private, Mention, Name, Ambient }

public sealed record ParsedGroupMessage(long GroupId, long SenderId, long MessageId,
    DateTimeOffset At, string Text, bool Mentioned, bool ImageSeen, bool IsPrivate = false);

public sealed record GroupChatPolicyOptions
{
    public IReadOnlyList<long> AllowedGroupIds { get; init; } = [];
    public IReadOnlyList<long> AllowedPrivateUserIds { get; init; } = [];
    public long SelfId { get; init; }
    public string[] BotNames { get; init; } = [];
    public GroupChatMode Mode { get; init; } = GroupChatMode.Mention;
    public TimeSpan MentionInterval { get; init; } = TimeSpan.FromSeconds(8);
    public int MentionPerHour { get; init; } = 30;
    public TimeSpan ActiveInterval { get; init; } = TimeSpan.FromSeconds(120);
    public int ActivePerHour { get; init; } = 6;
    public int ActiveMessageEvery { get; init; } = 3;
    public TimeSpan PrivateInterval { get; init; } = TimeSpan.FromSeconds(8);
    public int PrivatePerHour { get; init; } = 30;
    public TimeSpan EventTtl { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ReservationTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed record GroupChatDecision(bool ShouldReply, string Reason, Guid? ReservationId,
    ParsedGroupMessage Message, IReadOnlyList<ParsedGroupMessage> Context)
{
    public GroupChatTrigger Trigger { get; init; }
}

public sealed record GroupChatGroupStatus(long GroupId, bool Busy, bool ReservationCurrent,
    Guid? ReservationId, DateTimeOffset? ReservationExpiresAt, DateTimeOffset? LastReservedAt,
    int ReservedLastHour, int ContextCount, int ContextCharacters)
{
    public int DirectReservedLastHour { get; init; }
    public int ActiveReservedLastHour { get; init; }
    public DateTimeOffset? LastActiveReservedAt { get; init; }
    public int ActiveMessagesSinceOpportunity { get; init; }
    public int ActiveMessageEvery { get; init; }
    public int PendingActiveMessages { get; init; }
    public DateTimeOffset? ActiveDueAt { get; init; }
    public long? ActiveTriggerMessageId { get; init; }
}

public sealed record PrivateChatContactStatus(long UserId, bool Busy, bool ReservationCurrent,
    DateTimeOffset? LastReservedAt, int ReservedLastHour, int ContextCount, int ContextCharacters)
{
    public int PendingMessages { get; init; }
}

public sealed record GroupChatStatus(GroupChatMode Mode, bool Paused, int SeenEvents,
    IReadOnlyList<GroupChatGroupStatus> Groups)
{
    public IReadOnlyList<PrivateChatContactStatus> PrivateContacts { get; init; } = [];
}

/// <summary>
/// Local policy only: no network, AI, downloads, CQ execution, or message sending.
/// Decide atomically consumes each event and reserves an admitted candidate BEFORE an API call.
/// Check IsCurrent again immediately before sending. Expiry/pause invalidates a reservation
/// but never frees Busy: only matching Complete does, preventing overlapping late replies.
/// </summary>
public sealed class GroupChatPolicy
{
    public const int MaximumEventBytes = 64 * 1024;
    public const int MaximumSegments = 64;
    public const int MaximumTextCharacters = 1000;
    public const int MaximumContextMessages = 20;
    public const int MaximumContextCharacters = 4000;
    public const int MaximumSeenEvents = 2048;
    public static readonly TimeSpan MaximumEventAge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromSeconds(30);
    private readonly object gate = new();
    private readonly GroupChatPolicyOptions options;
    private readonly Func<DateTimeOffset> clock;
    private readonly HashSet<long> groups;
    private readonly HashSet<long> privateUsers;
    private readonly Dictionary<(bool IsPrivate, long Id), GroupState> states = [];
    private readonly Dictionary<(bool IsPrivate, long Id, long Message), DateTimeOffset> seen = [];
    private readonly Dictionary<Guid, (bool IsPrivate, long Id)> reservations = [];
    private DateTimeOffset? latestNow;
    private bool paused;

    public GroupChatPolicy(GroupChatPolicyOptions options, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SelfId <= 0 || options.AllowedGroupIds is null || options.AllowedGroupIds.Count == 0 ||
            options.AllowedGroupIds.Any(id => id <= 0) || options.BotNames is null ||
            options.AllowedPrivateUserIds is null || options.AllowedPrivateUserIds.Count > 16 ||
            options.AllowedPrivateUserIds.Any(id => id <= 0 || id == options.SelfId) ||
            options.BotNames.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 40) ||
            !Enum.IsDefined(options.Mode)) throw new ArgumentException("请明确配置授权群、selfId 和有效模式/称呼。", nameof(options));
        var minimumInterval = TimeSpan.FromSeconds(GroupChatSettings.MinimumIntervalSeconds);
        var maximumInterval = TimeSpan.FromSeconds(GroupChatSettings.MaximumIntervalSeconds);
        if (options.MentionInterval < minimumInterval || options.MentionInterval > maximumInterval ||
            options.ActiveInterval < minimumInterval || options.ActiveInterval > maximumInterval ||
            options.MentionPerHour is < 1 or > GroupChatSettings.MaximumPerHour || options.ActivePerHour is < 1 or > GroupChatSettings.MaximumPerHour ||
            options.PrivateInterval < minimumInterval || options.PrivateInterval > maximumInterval ||
            options.PrivatePerHour is < 1 or > GroupChatSettings.MaximumPerHour ||
            options.ActiveMessageEvery is < 1 or > 20 ||
            options.EventTtl < TimeSpan.FromMinutes(3) || options.EventTtl > TimeSpan.FromHours(24) ||
            options.ReservationTimeout <= TimeSpan.Zero || options.ReservationTimeout > TimeSpan.FromSeconds(150))
            throw new ArgumentException("回复间隔必须为3～3600秒、每小时上限必须为1～120次、主动机会每1～20条一次；事件去重期限为3分钟～24小时、回复期限为正数且最多150秒。", nameof(options));
        groups = options.AllowedGroupIds.ToHashSet();
        privateUsers = options.AllowedPrivateUserIds.ToHashSet();
        this.options = options with { AllowedGroupIds = groups.ToArray(), AllowedPrivateUserIds = privateUsers.ToArray(),
            BotNames = options.BotNames.Select(n => n.Trim()).Distinct().ToArray() };
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool TryParse(JsonElement root, out ParsedGroupMessage? parsed, out string? rejection,
        DateTimeOffset? now = null)
    {
        parsed = null;
        rejection = "invalid_event";
        if (root.ValueKind != JsonValueKind.Object) return false;
        var raw = root.GetRawText();
        if (raw.Length > MaximumEventBytes || Encoding.UTF8.GetByteCount(raw) > MaximumEventBytes)
        { rejection = "event_too_large"; return false; }
        var isPrivate = IsString(root, "message_type", "private");
        if (!IsString(root, "post_type", "message") || (!isPrivate && !IsString(root, "message_type", "group")) ||
            IsString(root, "sub_type", "self_sent") || IsString(root, "sub_type", "notice"))
        { rejection = "not_incoming_group_message"; return false; }
        if (!TryId(root, "self_id", out var self) || self != options.SelfId)
        { rejection = "wrong_self_id"; return false; }
        long group = 0;
        if (!isPrivate && (!TryId(root, "group_id", out group) || !groups.Contains(group)))
        { rejection = "group_not_allowed"; return false; }
        if (!TryId(root, "user_id", out var sender) || sender == options.SelfId ||
            !root.TryGetProperty("sender", out var senderObject) || senderObject.ValueKind != JsonValueKind.Object ||
            !TryId(senderObject, "user_id", out var senderCheck) || senderCheck != sender)
        { rejection = "invalid_or_self_sender"; return false; }
        if (isPrivate && !privateUsers.Contains(sender))
        { rejection = "private_user_not_allowed"; return false; }
        if (!TryId(root, "message_id", out var messageId, signed: true) ||
            !root.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out var seconds)) return false;
        DateTimeOffset at;
        try { at = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return false; }
        if (!Fresh(at, now ?? clock())) { rejection = "event_time_out_of_range"; return false; }
        if (!root.TryGetProperty("message", out var segments) || segments.ValueKind != JsonValueKind.Array ||
            segments.GetArrayLength() > MaximumSegments) return false;

        var text = new StringBuilder();
        var mentioned = false;
        var imageSeen = false;
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object || !segment.TryGetProperty("type", out var kind) ||
                kind.ValueKind != JsonValueKind.String) return false;
            var type = kind.GetString();
            if (type is not ("text" or "at" or "face" or "image")) continue;
            if (!segment.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
            switch (type)
            {
                case "text":
                    if (!data.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String) return false;
                    AppendBounded(text, CleanText(value.GetString()!));
                    break;
                case "at":
                    if (IsString(data, "qq", "all")) AppendBounded(text, "[@全体成员]");
                    else
                    {
                        if (!TryId(data, "qq", out var target)) return false;
                        if (target == options.SelfId) { mentioned = true; AppendBounded(text, "[@机器人]"); }
                        else AppendBounded(text, "[@成员]");
                    }
                    break;
                case "face":
                    if (!TryId(data, "id", out var face, signed: true) || face is < 0 or > 65535) return false;
                    AppendBounded(text, $"[表情:{face.ToString(CultureInfo.InvariantCulture)}]");
                    break;
                case "image":
                    imageSeen = true;
                    AppendBounded(text, "[图片]"); // Never read a URL, file, CQ code, or embedded payload.
                    break;
            }
        }
        var content = text.ToString().Trim();
        if (content.Length == 0) { rejection = "empty_supported_content"; return false; }
        parsed = new(group, sender, messageId, at, content, mentioned, imageSeen, isPrivate);
        rejection = null;
        return true;
    }

    public GroupChatDecision Decide(ParsedGroupMessage message, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (gate)
        {
            var at = Advance(now ?? clock());
            Prune(at);
            GroupChatDecision Reject(string reason, GroupState? state = null) =>
                new(false, reason, null, message, state?.Context.ToArray() ?? []) { Trigger = ClassifyTrigger(message) };
            if (message.IsPrivate ? message.GroupId != 0 || !privateUsers.Contains(message.SenderId) : !groups.Contains(message.GroupId))
                return Reject(message.IsPrivate ? "private_user_not_allowed" : "group_not_allowed");
            if (message.SenderId <= 0 || message.SenderId == options.SelfId) return Reject("invalid_or_self_sender");
            if (!Fresh(message.At, at)) return Reject("event_time_out_of_range");
            if (string.IsNullOrWhiteSpace(message.Text) || message.Text.Length > MaximumTextCharacters)
                return Reject("invalid_text");
            var conversation = Conversation(message);
            states.TryGetValue(conversation, out var state);
            var key = (conversation.IsPrivate, conversation.Id, message.MessageId);
            if (seen.ContainsKey(key)) return Reject("duplicate", state);
            // Fail closed instead of evicting fresh IDs and allowing an earlier candidate to retry.
            if (seen.Count >= MaximumSeenEvents) return Reject("event_capacity", state);
            seen.Add(key, at + options.EventTtl);
            if (state is null) { state = new GroupState(); states.Add(conversation, state); }
            AddContext(state, message);
            if (paused) return Reject("paused", state);
            if (options.Mode == GroupChatMode.Manual) return Reject("manual", state);
            if (message.IsPrivate)
            {
                // A contact's next short messages wait for its current reply/cooldown, without
                // spending additional attempts. Never keep a batch waiting for an hourly reset.
                if (state.DirectAttempts.Count >= options.PrivatePerHour)
                {
                    state.Pending.Clear();
                    return Reject("hourly_limit", state);
                }
                var waiting = state.Reservation is not null ||
                    state.LastReservedAt is { } previous && at - previous < options.PrivateInterval;
                if (waiting || state.Pending.Count > 0)
                {
                    if (!AddPending(state, message)) return Reject("private_pending_capacity", state);
                    if (waiting) return Reject("private_pending", state);
                    return ReservePrivate(conversation, state, TakePending(state), at);
                }
                return ReservePrivate(conversation, state, message, at);
            }
            var trigger = ClassifyTrigger(message);
            var addressed = trigger != GroupChatTrigger.Ambient;
            if (!addressed && (options.Mode == GroupChatMode.Mention || !IsActiveCandidate(message.Text)))
                return Reject("not_a_candidate", state);
            if (!addressed)
            {
                // Keep one bounded opportunity while generation or a cooldown is in progress.
                // A full sample slides to the latest ordinary messages instead of building a queue.
                if (state.ActiveAttempts.Count >= options.ActivePerHour)
                {
                    ClearActivePending(state);
                    return Reject("hourly_limit", state);
                }
                if (state.ActiveSamples.Count >= options.ActiveMessageEvery) state.ActiveSamples.RemoveAt(0);
                state.ActiveSamples.Add(message.At);
                state.PendingActiveTrigger = message;
                if (state.Reservation is not null) return Reject("busy", state);
                if (state.LastReservedAt is { } previous && at - previous < options.MentionInterval)
                    return Reject("cooldown", state);
                if (state.LastActiveReservedAt is { } previousActive && at - previousActive < options.ActiveInterval)
                    return Reject("active_cooldown", state);
                if (state.ActiveSamples.Count < options.ActiveMessageEvery) return Reject("active_waiting", state);
                return ReserveActive(conversation, state, at);
            }
            if (state.Reservation is not null) return Reject("busy", state);
            // Direct replies never reset the ambient interval. Keep a short gap after any reply
            // so a newly available ambient opportunity cannot follow a direct reply immediately.
            if (state.LastReservedAt is { } last && at - last < options.MentionInterval)
                return Reject("cooldown", state);
            if (state.DirectAttempts.Count >= options.MentionPerHour) return Reject("hourly_limit", state);
            return Reserve(conversation, state, message, at, trigger, state.DirectAttempts);
        }
    }

    /// <summary>Offer one fresh ordinary-message batch per idle group when its own limits permit.</summary>
    public IReadOnlyList<GroupChatDecision> TakeReadyActiveReplies(DateTimeOffset? now = null)
    {
        lock (gate)
        {
            var at = Advance(now ?? clock());
            Prune(at);
            if (paused || options.Mode != GroupChatMode.Active) return [];
            var ready = new List<GroupChatDecision>();
            foreach (var (conversation, state) in states)
            {
                if (conversation.IsPrivate || state.PendingActiveTrigger is null) continue;
                if (state.ActiveAttempts.Count >= options.ActivePerHour)
                {
                    ClearActivePending(state);
                    continue;
                }
                if (state.Reservation is not null || state.ActiveSamples.Count < options.ActiveMessageEvery ||
                    state.LastReservedAt is { } previous && at - previous < options.MentionInterval ||
                    state.LastActiveReservedAt is { } previousActive && at - previousActive < options.ActiveInterval) continue;
                ready.Add(ReserveActive(conversation, state, at));
            }
            return ready;
        }
    }

    private GroupChatDecision ReserveActive((bool IsPrivate, long Id) conversation, GroupState state, DateTimeOffset at)
    {
        var trigger = state.PendingActiveTrigger ?? throw new InvalidOperationException("主动回复缺少触发消息。");
        ClearActivePending(state);
        state.LastActiveReservedAt = at;
        return Reserve(conversation, state, trigger, at, GroupChatTrigger.Ambient, state.ActiveAttempts);
    }

    private static void ClearActivePending(GroupState state)
    {
        state.ActiveSamples.Clear();
        state.PendingActiveTrigger = null;
    }

    private DateTimeOffset? ActiveDueAt(GroupState state, DateTimeOffset at)
    {
        if (paused || options.Mode != GroupChatMode.Active || state.Reservation is not null || state.PendingActiveTrigger is null ||
            state.ActiveSamples.Count < options.ActiveMessageEvery || state.ActiveAttempts.Count >= options.ActivePerHour) return null;
        var due = at;
        if (state.LastReservedAt is { } last && last + options.MentionInterval > due) due = last + options.MentionInterval;
        if (state.LastActiveReservedAt is { } active && active + options.ActiveInterval > due) due = active + options.ActiveInterval;
        return due;
    }

    /// <summary>Admit fresh, bounded private batches only after that contact is idle and its cooldown elapsed.</summary>
    public IReadOnlyList<GroupChatDecision> TakeReadyPrivateReplies(DateTimeOffset? now = null)
    {
        lock (gate)
        {
            var at = Advance(now ?? clock());
            Prune(at);
            if (paused || options.Mode == GroupChatMode.Manual) return [];
            var ready = new List<GroupChatDecision>();
            foreach (var (conversation, state) in states)
            {
                if (!conversation.IsPrivate || state.Pending.Count == 0) continue;
                if (state.DirectAttempts.Count >= options.PrivatePerHour)
                {
                    state.Pending.Clear();
                    continue;
                }
                if (state.Reservation is not null ||
                    state.LastReservedAt is { } previous && at - previous < options.PrivateInterval) continue;
                ready.Add(ReservePrivate(conversation, state, TakePending(state), at));
            }
            return ready;
        }
    }

    private GroupChatDecision ReservePrivate((bool IsPrivate, long Id) conversation, GroupState state,
        ParsedGroupMessage message, DateTimeOffset at)
        => Reserve(conversation, state, message, at, GroupChatTrigger.Private, state.DirectAttempts);

    private GroupChatDecision Reserve((bool IsPrivate, long Id) conversation, GroupState state,
        ParsedGroupMessage message, DateTimeOffset at, GroupChatTrigger trigger, Queue<DateTimeOffset> attempts)
    {
        var reservation = new Reservation(Guid.NewGuid(), at + options.ReservationTimeout);
        state.Reservation = reservation;
        state.LastReservedAt = at;
        attempts.Enqueue(at); // Failure also consumes the attempt; no retry loop.
        reservations.Add(reservation.Id, conversation);
        return new(true, "reserved", reservation.Id, message, state.Context.ToArray()) { Trigger = trigger };
    }

    private static bool AddPending(GroupState state, ParsedGroupMessage message)
    {
        if (state.Pending.Count >= MaximumContextMessages ||
            state.Pending.Sum(pending => pending.Text.Length) + state.Pending.Count + message.Text.Length > MaximumContextCharacters)
            return false;
        state.Pending.Add(message);
        return true;
    }

    private static ParsedGroupMessage TakePending(GroupState state)
    {
        var message = state.Pending[^1] with
        {
            Text = string.Join("\n", state.Pending.Select(pending => pending.Text)),
            Mentioned = state.Pending.Any(pending => pending.Mentioned),
            ImageSeen = state.Pending.Any(pending => pending.ImageSeen)
        };
        state.Pending.Clear();
        return message;
    }

    public bool IsCurrent(Guid reservationId, DateTimeOffset? now = null)
    {
        lock (gate)
        {
            var at = Advance(now ?? clock());
            return !paused && reservations.TryGetValue(reservationId, out var group) &&
                states[group].Reservation is { Valid: true } reservation &&
                reservation.Id == reservationId && at < reservation.ExpiresAt;
        }
    }

    public bool Complete(Guid reservationId, bool succeeded, DateTimeOffset? now = null)
    {
        lock (gate)
        {
            Prune(Advance(now ?? clock()));
            if (!reservations.TryGetValue(reservationId, out var group) ||
                states[group].Reservation?.Id != reservationId) return false;
            states[group].Reservation = null;
            reservations.Remove(reservationId);
            return true; // succeeded is intentionally irrelevant to deduplication and rate limits.
        }
    }

    /// <summary>Call only after OneBot confirms a live send succeeded; never for previews.</summary>
    public void RecordOwnReply(long groupId, string text, DateTimeOffset? now = null)
        => RecordOwnReply((false, groupId), text, now);

    public void RecordOwnPrivateReply(long userId, string text, DateTimeOffset? now = null)
        => RecordOwnReply((true, userId), text, now);

    private void RecordOwnReply((bool IsPrivate, long Id) conversation, string text, DateTimeOffset? now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!(conversation.IsPrivate ? privateUsers : groups).Contains(conversation.Id))
            throw new ArgumentException("不得记录未授权会话的回复。", nameof(conversation));
        var bounded = new StringBuilder();
        var clean = CleanText(text);
        var take = Math.Min(clean.Length, OneBotMessageClient.MaximumTextCharacters + 80);
        if (take > 0 && char.IsHighSurrogate(clean[take - 1])) take--;
        bounded.Append(clean.AsSpan(0, take));
        var content = bounded.ToString().Trim();
        if (content.Length == 0) return;
        lock (gate)
        {
            var at = Advance(now ?? clock());
            Prune(at);
            if (!states.TryGetValue(conversation, out var state)) { state = new GroupState(); states.Add(conversation, state); }
            // Context metadata only. It never enters Decide, event deduplication, or reservations.
            AddContext(state, new(conversation.IsPrivate ? 0 : conversation.Id, options.SelfId, 0, at, content, false, false, conversation.IsPrivate));
        }
    }

    public void SetPaused(bool value)
    {
        lock (gate)
        {
            paused = value;
            if (value)
                foreach (var state in states.Values)
                {
                    state.Pending.Clear();
                    ClearActivePending(state);
                    if (state.Reservation is { } reservation) reservation.Valid = false;
                }
        }
    }

    public GroupChatStatus Snapshot(DateTimeOffset? now = null)
    {
        lock (gate)
        {
            var at = Advance(now ?? clock());
            Prune(at);
            return new(options.Mode, paused, seen.Count, states.Where(pair => !pair.Key.IsPrivate).OrderBy(pair => pair.Key.Id).Select(pair =>
            {
                var state = pair.Value;
                var reservation = state.Reservation;
                return new GroupChatGroupStatus(pair.Key.Id, reservation is not null,
                    !paused && reservation is { Valid: true } && at < reservation.ExpiresAt,
                    reservation?.Id, reservation?.ExpiresAt, state.LastReservedAt,
                    state.DirectAttempts.Count + state.ActiveAttempts.Count, state.Context.Count, state.Context.Sum(m => m.Text.Length))
                {
                    DirectReservedLastHour = state.DirectAttempts.Count,
                    ActiveReservedLastHour = state.ActiveAttempts.Count,
                    LastActiveReservedAt = state.LastActiveReservedAt,
                    ActiveMessagesSinceOpportunity = state.ActiveSamples.Count,
                    ActiveMessageEvery = options.ActiveMessageEvery,
                    PendingActiveMessages = state.ActiveSamples.Count,
                    ActiveDueAt = ActiveDueAt(state, at),
                    ActiveTriggerMessageId = state.PendingActiveTrigger?.MessageId
                };
            }).ToArray())
            {
                PrivateContacts = states.Where(pair => pair.Key.IsPrivate).OrderBy(pair => pair.Key.Id).Select(pair =>
                {
                    var state = pair.Value;
                    return new PrivateChatContactStatus(pair.Key.Id, state.Reservation is not null,
                        !paused && state.Reservation is { Valid: true } reservation && at < reservation.ExpiresAt,
                        state.LastReservedAt, state.DirectAttempts.Count, state.Context.Count, state.Context.Sum(m => m.Text.Length))
                    { PendingMessages = state.Pending.Count };
                }).ToArray()
            };
        }
    }

    private DateTimeOffset Advance(DateTimeOffset now)
    {
        if (latestNow is { } previous && now < previous) return previous;
        latestNow = now;
        return now;
    }

    private static (bool IsPrivate, long Id) Conversation(ParsedGroupMessage message) =>
        (message.IsPrivate, message.IsPrivate ? message.SenderId : message.GroupId);

    private GroupChatTrigger ClassifyTrigger(ParsedGroupMessage message) => message.IsPrivate ? GroupChatTrigger.Private :
        message.Mentioned ? GroupChatTrigger.Mention :
        options.BotNames.Any(name => StartsWithName(message.Text, name)) ? GroupChatTrigger.Name : GroupChatTrigger.Ambient;

    private static void AddContext(GroupState state, ParsedGroupMessage message)
    {
        var insert = state.Context.FindIndex(previous => previous.At > message.At);
        if (insert < 0) state.Context.Add(message);
        else state.Context.Insert(insert, message); // Stable arrival order within one-second timestamps.
        while (state.Context.Count > MaximumContextMessages || state.Context.Sum(m => m.Text.Length) > MaximumContextCharacters)
            state.Context.RemoveAt(0);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in seen.Where(pair => pair.Value < now).Select(pair => pair.Key).ToArray()) seen.Remove(key);
        foreach (var state in states.Values)
        {
            while (state.DirectAttempts.TryPeek(out var direct) && now - direct >= TimeSpan.FromHours(1)) state.DirectAttempts.Dequeue();
            while (state.ActiveAttempts.TryPeek(out var active) && now - active >= TimeSpan.FromHours(1)) state.ActiveAttempts.Dequeue();
            state.Context.RemoveAll(message => now - message.At > options.EventTtl);
            state.Pending.RemoveAll(message => !Fresh(message.At, now));
            state.ActiveSamples.RemoveAll(sample => !Fresh(sample, now));
            if (state.ActiveSamples.Count == 0 || state.PendingActiveTrigger is { } trigger && !Fresh(trigger.At, now))
                ClearActivePending(state);
            if (state.ActiveAttempts.Count >= options.ActivePerHour) ClearActivePending(state);
            // Expiry invalidates sending, but deliberately does NOT release Busy.
            if (state.Reservation is { } reservation && now >= reservation.ExpiresAt) reservation.Valid = false;
        }
    }

    private static bool Fresh(DateTimeOffset at, DateTimeOffset now) =>
        now - at <= MaximumEventAge && at - now <= MaximumFutureSkew;

    private static bool StartsWithName(string text, string name)
    {
        text = text.TrimStart();
        if (!text.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;
        return text.Length == name.Length || !char.IsAsciiLetterOrDigit(name[^1]) ||
            !char.IsAsciiLetterOrDigit(text[name.Length]);
    }

    private static bool IsActiveCandidate(string text)
    {
        // Parser summaries are context only; their labels and numeric IDs are not ordinary words.
        var ordinaryText = Regex.Replace(text, @"\[[^\]\r\n]*\]", "");
        return ordinaryText.EnumerateRunes().Count(Rune.IsLetterOrDigit) >= 2;
    }

    private static string CleanText(string text) => new string(text.Where(ch => !char.IsControl(ch) || ch is '\n' or '\t').ToArray())
        .Replace("[CQ:", "[文本CQ:", StringComparison.OrdinalIgnoreCase);

    private static void AppendBounded(StringBuilder text, string value)
    {
        var take = Math.Min(value.Length, MaximumTextCharacters - text.Length);
        if (take > 0 && char.IsHighSurrogate(value[take - 1])) take--;
        if (take > 0) text.Append(value.AsSpan(0, take));
    }

    private static bool IsString(JsonElement root, string name, string expected) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    private static bool TryId(JsonElement root, string name, out long id, bool signed = false)
    {
        id = 0;
        if (!root.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt64(out id) && (signed || id > 0);
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(),
            signed ? NumberStyles.AllowLeadingSign : NumberStyles.None, CultureInfo.InvariantCulture, out id) && (signed || id > 0);
    }

    private sealed class GroupState
    {
        public List<ParsedGroupMessage> Context { get; } = [];
        public List<ParsedGroupMessage> Pending { get; } = [];
        public Queue<DateTimeOffset> DirectAttempts { get; } = new();
        public Queue<DateTimeOffset> ActiveAttempts { get; } = new();
        public DateTimeOffset? LastReservedAt { get; set; }
        public DateTimeOffset? LastActiveReservedAt { get; set; }
        public List<DateTimeOffset> ActiveSamples { get; } = [];
        public ParsedGroupMessage? PendingActiveTrigger { get; set; }
        public Reservation? Reservation { get; set; }
    }

    private sealed class Reservation(Guid id, DateTimeOffset expiresAt)
    {
        public Guid Id { get; } = id;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public bool Valid { get; set; } = true;
    }
}
