namespace AudioBridge;

public sealed record ScheduledMessageDraft
{
    public string Name { get; init; } = "定时消息";
    public bool Enabled { get; init; }
    public string TargetType { get; init; } = "group";
    public long TargetId { get; init; }
    public string ScheduleType { get; init; } = "once";
    public DateTimeOffset? OnceAt { get; init; }
    public string? DailyTime { get; init; }
    public string TimeZone { get; init; } = "Asia/Shanghai";
    public string Text { get; init; } = "";
    public string? MemeId { get; init; }
}

public sealed record ScheduledMessageTask(string Id, long Revision, ScheduledMessageDraft Definition,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset EvaluatedThrough, ScheduledMessageRun? LastRun);

/// <summary>A durable reservation only. A caller must recheck it immediately before sending.</summary>
public sealed record ScheduledMessageClaim(string TaskId, long Revision, string OccurrenceId,
    DateTimeOffset DueAt, ScheduledMessageDraft Definition);

/// <summary>One latest result per task; contains no conversation history or model context.</summary>
public sealed record ScheduledMessageRun(string OccurrenceId, DateTimeOffset DueAt, DateTimeOffset ClaimedAt,
    DateTimeOffset? CompletedAt, string Status, string? MessageId);
