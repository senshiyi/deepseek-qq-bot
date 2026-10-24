using System.Globalization;

namespace AudioBridge;

public sealed record ScheduledMessageScope(long SelfId, long[] Groups, long[] PrivateUsers, bool Paused);
public sealed record ScheduledTargetUsage(string TargetType, long TargetId, int AttemptsLastHour, DateTimeOffset? NextAllowedAt);

/// <summary>One host-wide allowance survives Apply/reload and is independent of conversational reply limits.</summary>
public sealed class ScheduledMessageRateLimits(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<(string Type, long Id), Queue<DateTimeOffset>> attempts = [];
    public bool Reserve(ScheduledMessageDraft task)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow(); Trim(now);
            var key = (task.TargetType, task.TargetId);
            if (!attempts.TryGetValue(key, out var values)) attempts[key] = values = new();
            if (values.Count >= ScheduledMessageRuntime.MaximumPerHour || values.Count > 0 && now < values.Last() + ScheduledMessageRuntime.MinimumInterval) return false;
            values.Enqueue(now); return true;
        }
    }
    public ScheduledTargetUsage[] Snapshot()
    {
        lock (gate)
        {
            Trim(clock.GetUtcNow());
            return attempts.Select(pair => new ScheduledTargetUsage(pair.Key.Type, pair.Key.Id, pair.Value.Count,
                pair.Value.Count == 0 ? null : pair.Value.Count >= ScheduledMessageRuntime.MaximumPerHour
                    ? pair.Value.Peek().AddHours(1) : pair.Value.Last() + ScheduledMessageRuntime.MinimumInterval)).ToArray();
        }
    }
    private void Trim(DateTimeOffset now)
    {
        foreach (var pair in attempts.ToArray())
        {
            while (pair.Value.TryPeek(out var at) && now - at >= TimeSpan.FromHours(1)) pair.Value.Dequeue();
            if (pair.Value.Count == 0) attempts.Remove(pair.Key);
        }
    }
}

/// <summary>
/// Durable claims feed the chat runtime's single worker. This class never starts a sending loop;
/// the owner must await HandleAsync alongside ordinary replies, and poll even while paused.
/// </summary>
public sealed class ScheduledMessageRuntime(ScheduledMessageStore store, MemeLibrary memes, OneBotMessageClient client,
    long expectedSelfId, bool live, Func<ScheduledMessageScope> scope,
    Action<ScheduledMessageDraft, string, string>? confirmed = null, Action? accountChanged = null,
    TimeProvider? timeProvider = null, ScheduledMessageRateLimits? rateLimits = null) : IAsyncDisposable
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumSchedulingGap = TimeSpan.FromSeconds(5);
    public const int MaximumPerHour = 6;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ScheduledMessageRateLimits limits = rateLimits ?? new(timeProvider);
    private readonly object gate = new();
    private readonly Dictionary<string, (string Occurrence, long Epoch)> issued = [];
    private long epoch;
    private CancellationTokenSource? active;
    private string? activeId;
    private string? storageError;
    private DateTimeOffset? lastObservedAt, offlineCutoff;
    private bool activeSubmitted;
    private bool paused, disposed;

    public IReadOnlyList<ScheduledMessageClaim> Poll(DateTimeOffset sessionStartedAt)
    {
        ObserveContinuity();
        var current = scope();
        lock (gate)
        {
            var claims = store.ClaimDue(current.Groups, current.PrivateUsers, current.Paused || paused || disposed, sessionStartedAt);
            var ready = new List<ScheduledMessageClaim>();
            foreach (var claim in claims)
            {
                if (Offline(claim)) { store.Complete(claim, "skipped_offline"); continue; }
                issued[claim.TaskId] = (claim.OccurrenceId, epoch); ready.Add(claim);
            }
            if (issued.Count > ScheduledMessageStore.MaximumTasks)
            {
                var ids = store.List().Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var key in issued.Keys.Where(id => !ids.Contains(id)).ToArray()) issued.Remove(key);
            }
            return ready;
        }
    }

    /// <summary>Check the actual QQ connection before due work enters the serial sending queue.</summary>
    public async Task<IReadOnlyList<ScheduledMessageClaim>> PollAsync(DateTimeOffset sessionStartedAt, CancellationToken ct)
    {
        var claims = Poll(sessionStartedAt);
        if (!live || claims.Count == 0) return claims;
        try
        {
            var status = await client.GetStatusAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (status.Online && status.Good)
                return claims.Where(Current).ToArray();
        }
        catch (OneBotMessageException) { ct.ThrowIfCancellationRequested(); }
        NotifyOffline();
        return [];
    }

    /// <summary>Observed offline time invalidates all unsent occurrences up to that point; reconnect never catches them up.</summary>
    public void NotifyOffline()
    {
        lock (gate) MarkOffline(clock.GetUtcNow());
    }

    public object Snapshot()
    {
        lock (gate)
        {
            return new { minimumIntervalSeconds = 60, perTargetPerHour = MaximumPerHour,
                maximumSchedulingGapSeconds = 5, offlineCutoff,
                activeTaskId = activeId, lastError = storageError, targets = limits.Snapshot() };
        }
    }

    public void Invalidate(string taskId)
    {
        lock (gate)
        {
            if (activeId == taskId) active?.Cancel();
        }
    }

    public Task SetPausedAsync(bool value)
    {
        lock (gate)
        {
            if (!value && paused)
            {
                // A user can pause and resume between scheduler ticks. Consume due work while still paused.
                var current = scope();
                store.ClaimDue(current.Groups, current.PrivateUsers, true, clock.GetUtcNow());
            }
            paused = value;
            if (value) { epoch++; active?.Cancel(); }
        }
        if (value) CancelPending("skipped_paused");
        return Task.CompletedTask;
    }

    public async Task<GroupChatOutcome> HandleAsync(ScheduledMessageClaim claim, CancellationToken ct)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (gate)
        {
            if (disposed || active is not null) { store.Complete(claim, "skipped_busy"); return new("skipped_busy"); }
            active = work; activeId = claim.TaskId; activeSubmitted = false;
        }
        var submitting = false;
        GroupChatOutcome Finish(string status, string? messageId = null)
        { store.Complete(claim, status, messageId); return new(status, MessageId: messageId); }
        try
        {
            work.Token.ThrowIfCancellationRequested();
            if (!Current(claim)) return Finish(Offline(claim) ? "skipped_offline" : "skipped_not_authorized");
            var definition = claim.Definition;
            // Resolve from the curated library again at execution; never accept task-supplied paths or URLs.
            var segments = new List<OneBotSegment>();
            if (definition.Text.Length > 0) segments.Add(OneBotSegment.Text(definition.Text));
            if (definition.MemeId is { } meme) segments.Add(memes.ResolveSegment(meme));
            if (!live) return Finish("preview");
            var logged = await client.GetLoginInfoAsync(work.Token).ConfigureAwait(false);
            work.Token.ThrowIfCancellationRequested();
            if (logged != expectedSelfId.ToString(CultureInfo.InvariantCulture) || scope().SelfId != expectedSelfId)
            {
                var outcome = Finish("account_changed_paused");
                await SetPausedAsync(true).ConfigureAwait(false);
                accountChanged?.Invoke();
                return outcome;
            }
            var connection = await client.GetStatusAsync(work.Token).ConfigureAwait(false);
            work.Token.ThrowIfCancellationRequested();
            if (!connection.Online || !connection.Good)
            {
                NotifyOffline();
                return Finish("skipped_offline");
            }
            if (!Current(claim)) return Finish(Offline(claim) ? "skipped_offline" : "skipped_not_authorized");
            if (!limits.Reserve(definition)) return Finish("skipped_busy");
            work.Token.ThrowIfCancellationRequested();
            // An edit/pause racing after HTTP submission cannot retract a message. Its receipt or uncertainty is retained.
            lock (gate)
            {
                ObserveContinuity();
                if (Offline(claim)) return Finish("skipped_offline");
                work.Token.ThrowIfCancellationRequested();
                submitting = true; activeSubmitted = true;
            }
            var receipt = definition.TargetType == "private"
                ? await client.SendPrivateAsync(new(definition.TargetId.ToString(CultureInfo.InvariantCulture), segments), work.Token).ConfigureAwait(false)
                : await client.SendGroupAsync(new(definition.TargetId.ToString(CultureInfo.InvariantCulture), segments), work.Token).ConfigureAwait(false);
            var spoken = definition.Text + (definition.MemeId is null ? "" : " [表情包:" + definition.MemeId + "]");
            var accepted = Finish("api_accepted", receipt.MessageId);
            try { confirmed?.Invoke(definition, spoken, receipt.MessageId); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
            { lock (gate) storageError = error.GetType().Name; }
            return accepted;
        }
        catch (OperationCanceledException)
        { return Finish(submitting ? "delivery_unknown" : Offline(claim) ? "skipped_offline" : "cancelled"); }
        catch (OneBotMessageException error)
        {
            if (!submitting && !work.IsCancellationRequested) NotifyOffline();
            return Finish(error.DeliveryMayHaveOccurred ? "delivery_unknown" : work.IsCancellationRequested ? Offline(claim) ? "skipped_offline" : "cancelled" : "failed");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
        { return Finish(submitting ? "delivery_unknown" : "failed"); }
        finally
        {
            lock (gate) if (active == work) { active = null; activeId = null; activeSubmitted = false; }
        }
    }

    private bool Current(ScheduledMessageClaim claim)
    {
        ObserveContinuity();
        var current = scope();
        lock (gate)
            if (Offline(claim) || issued.TryGetValue(claim.TaskId, out var item) && (item.Occurrence != claim.OccurrenceId || item.Epoch != epoch)) return false;
        return current.SelfId == expectedSelfId && store.IsCurrentAndAuthorized(claim, current.Groups, current.PrivateUsers,
            current.Paused || IsPaused());
    }

    private bool IsPaused() { lock (gate) return paused || disposed; }
    private bool Offline(ScheduledMessageClaim claim)
    { lock (gate) return offlineCutoff is { } cutoff && claim.DueAt <= cutoff; }
    private void ObserveContinuity()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var resumed = lastObservedAt is { } previous && now - previous > MaximumSchedulingGap;
            if (lastObservedAt is null || now > lastObservedAt) lastObservedAt = now;
            if (!resumed) return;
            MarkOffline(now);
        }
    }
    private void MarkOffline(DateTimeOffset now)
    {
        if (offlineCutoff is null || now > offlineCutoff) offlineCutoff = now;
        epoch++; active?.Cancel();
        // Includes work already queued behind a conversational reply. Submitted HTTP remains uncertain.
        foreach (var task in store.List())
            if (task.LastRun is { Status: "claimed" } run && run.DueAt <= now)
                store.Complete(new(task.Id, task.Revision, run.OccurrenceId, run.DueAt, task.Definition),
                    activeId == task.Id && activeSubmitted ? "delivery_unknown" : "skipped_offline");
    }
    private void CancelPending(string status)
    {
        try
        {
            foreach (var task in store.List())
                if (task.LastRun is { Status: "claimed" } run)
                    store.Complete(new(task.Id, task.Revision, run.OccurrenceId, run.DueAt, task.Definition), status);
            lock (gate) storageError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
        { lock (gate) storageError = error.GetType().Name; }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) { disposed = true; paused = true; epoch++; active?.Cancel(); }
        CancelPending("cancelled");
        return ValueTask.CompletedTask;
    }
}
