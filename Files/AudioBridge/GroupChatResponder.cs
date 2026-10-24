namespace AudioBridge;

public sealed record GroupChatOutcome(string Status, string? Text = null, string? MemeId = null, string? MessageId = null);

public sealed class GroupChatResponder(GroupChatPolicy policy, GroupChatBrain brain, MemeLibrary memes,
    OneBotMessageClient client, bool live, long expectedSelfId,
    Action<ParsedGroupMessage, string, string>? confirmedReply = null,
    Func<DateTimeOffset>? clock = null) : IAsyncDisposable
{
    private static readonly TimeSpan MemeRepeatWindow = TimeSpan.FromSeconds(120);
    private const int MaximumRecentMemeSessions = 32;
    private readonly string expectedAccount = expectedSelfId > 0 ? expectedSelfId.ToString(System.Globalization.CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(expectedSelfId));
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly object gate = new();
    // Accessed under sendGate only. A successful text-only reply does not reset this short window.
    private readonly Dictionary<(bool IsPrivate, long Id), (string MemeId, DateTimeOffset At)> recentMemes = [];
    private CancellationTokenSource? active;
    private bool disposed;

    public async Task<GroupChatOutcome> HandleAsync(GroupChatDecision decision, CancellationToken ct)
    {
        if (!decision.ShouldReply || decision.ReservationId is not { } id) return new("ignored");
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (gate)
        {
            if (disposed) { policy.Complete(id, false); throw new ObjectDisposedException(nameof(GroupChatResponder)); }
            if (active is not null) { policy.Complete(id, false); return new("busy"); }
            active = work;
        }
        var succeeded = false;
        try
        {
            if (!policy.IsCurrent(id)) return new("stale");
            var generating = brain.GenerateAsync(decision.Context, work.Token, decision.Message, decision.Trigger);
            _ = generating.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var reply = await generating.WaitAsync(work.Token).ConfigureAwait(false);
            work.Token.ThrowIfCancellationRequested();
            if (!reply.Send) return new("model_skipped");
            await sendGate.WaitAsync(work.Token).ConfigureAwait(false);
            try
            {
                work.Token.ThrowIfCancellationRequested();
                if (!policy.IsCurrent(id)) return new("stale");
                var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
                foreach (var expired in recentMemes.Where(item => now - item.Value.At >= MemeRepeatWindow).Select(item => item.Key).ToArray())
                    recentMemes.Remove(expired);
                var session = (decision.Message.IsPrivate, decision.Message.IsPrivate ? decision.Message.SenderId : decision.Message.GroupId);
                var memeId = reply.MemeId;
                if (!reply.ExplicitMemeRequest && memeId is not null && recentMemes.TryGetValue(session, out var recent) && recent.MemeId == memeId)
                    memeId = null;
                if (reply.Text.Length == 0 && memeId is null) return new("repeated_meme_suppressed");
                var segments = new List<OneBotSegment>();
                if (reply.Text.Length > 0) segments.Add(OneBotSegment.Text(reply.Text));
                if (memeId is not null) segments.Add(memes.ResolveSegment(memeId));
                if (!live) { succeeded = true; return new("preview", reply.Text, memeId); }
                if (await client.GetLoginInfoAsync(work.Token).ConfigureAwait(false) != expectedAccount)
                {
                    policy.SetPaused(true);
                    return new("account_changed_paused");
                }
                work.Token.ThrowIfCancellationRequested();
                if (!policy.IsCurrent(id)) return new("stale");
                var receipt = decision.Message.IsPrivate
                    ? await client.SendPrivateAsync(new(decision.Message.SenderId.ToString(System.Globalization.CultureInfo.InvariantCulture), segments), work.Token).ConfigureAwait(false)
                    : await client.SendGroupAsync(new(decision.Message.GroupId.ToString(System.Globalization.CultureInfo.InvariantCulture), segments), work.Token).ConfigureAwait(false);
                succeeded = true;
                if (memeId is not null)
                {
                    if (!recentMemes.ContainsKey(session) && recentMemes.Count >= MaximumRecentMemeSessions)
                        recentMemes.Remove(recentMemes.MinBy(item => item.Value.At).Key);
                    recentMemes[session] = (memeId, clock?.Invoke() ?? DateTimeOffset.UtcNow);
                }
                var spoken = reply.Text + (memeId is null ? "" : " [表情包:" + memeId + "]");
                if (decision.Message.IsPrivate) policy.RecordOwnPrivateReply(decision.Message.SenderId, spoken);
                else policy.RecordOwnReply(decision.Message.GroupId, spoken);
                confirmedReply?.Invoke(decision.Message, reply.Text.Length > 0 ? reply.Text : spoken, receipt.MessageId);
                return new("api_accepted", reply.Text, memeId, receipt.MessageId);
            }
            finally { sendGate.Release(); }
        }
        finally
        {
            policy.Complete(id, succeeded);
            lock (gate) if (active == work) active = null;
        }
    }

    public async Task SetPausedAsync(bool paused)
    {
        policy.SetPaused(paused);
        if (!paused) return;
        CancellationTokenSource? pending;
        lock (gate) pending = active;
        if (pending is not null)
            try { await pending.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        await sendGate.WaitAsync().ConfigureAwait(false);
        sendGate.Release(); // An already submitted HTTP request may have delivered; it is never retried.
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        await SetPausedAsync(true).ConfigureAwait(false);
        // Keep the tiny gate alive for a late fake/provider task; it can never pass policy.IsCurrent.
    }
}
