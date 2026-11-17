namespace AudioBridge;

public sealed record GroupChatOutcome(string Status, string? Text = null, string? MemeId = null, string? MessageId = null,
    string? ImageUrl = null, bool Voice = false);

/// <summary>一次语音合成：朗读稿、模型标注的情绪，以及这是谁的一轮（本地按他的角色状态兜底语气）。</summary>
public sealed record VoiceCue(string Text, string? Emotion, long UserId, bool Private);

public sealed class GroupChatResponder(GroupChatPolicy policy, GroupChatBrain brain, MemeLibrary memes,
    OneBotMessageClient client, bool live, long expectedSelfId,
    Action<ParsedGroupMessage, string, string>? confirmedReply = null,
    Func<DateTimeOffset>? clock = null,
    Func<string, CancellationToken, Task<byte[]>>? fetchImage = null,
    Func<VoiceCue, CancellationToken, Task<byte[]>>? synthesizeVoice = null) : IAsyncDisposable
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
            // 图片和语音在发送闸门前准备：下载/合成失败只降级为纯文字，不影响文字本身。
            var imageBytes = reply.ImageUrl is { } imageUrl && fetchImage is not null
                ? await TryAsync(() => fetchImage(imageUrl, work.Token)).ConfigureAwait(false) : null;
            // 语音优先：先把整条合成完，成功就只发语音（文字就是朗读稿，不再单独发一遍文字）；失败才退回文字。
            var voiceTask = reply.Voice && synthesizeVoice is not null
                ? TryAsync(() => synthesizeVoice(
                    new(reply.Text, reply.Emotion, decision.Message.SenderId, decision.Message.IsPrivate), work.Token))
                : null;
            var voiceWav = voiceTask is null ? null : await voiceTask.ConfigureAwait(false);
            if (voiceWav is not null)
            {
                try { WaveForm.Validate(voiceWav); }
                catch (InvalidDataException) { voiceWav = null; }
            }
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
                // 语音发出去时表情和图片都不跟着发：这一轮的内容就是那段声音。
                if (voiceWav is not null) { memeId = null; imageBytes = null; }
                if (reply.Text.Length == 0 && memeId is null && imageBytes is null && voiceWav is null) return new("repeated_meme_suppressed");
                // 文字路径先把消息备好（表情/图片校验失败要在任何 HTTP 之前失败）；语音路径只有一段录音。
                List<List<OneBotSegment>>? messages = null;
                if (voiceWav is null)
                {
                    // 多条消息只在模型显式给 lines 时发生；text 永远是一条（换行留在同一气泡里）。
                    var texts = (reply.Messages is { Length: > 0 } chosen ? chosen : [reply.Text]).Where(text => text.Length > 0).ToList();
                    messages = texts.Select(text => new List<OneBotSegment> { OneBotSegment.Text(text) }).ToList();
                    if (messages.Count == 0 && (memeId is not null || imageBytes is not null)) messages.Add([]);
                    if (memeId is not null)
                    {
                        try { messages[^1].Add(memes.ResolveSegment(memeId)); }
                        catch (InvalidDataException error) { throw ReplyFailure.Tag(error, "meme_data_invalid"); }
                    }
                    if (imageBytes is not null)
                    {
                        try { messages[^1].Add(OneBotSegment.Image(imageBytes)); }
                        catch (InvalidDataException error) { throw ReplyFailure.Tag(error, "web_image_invalid"); }
                    }
                    // 群里"插话"是对某一条消息的反应：引用它，别人才看得出在答谁（直接@或点名的回复不需要）。
                    var quoting = !decision.Message.IsPrivate && decision.Trigger == GroupChatTrigger.Ambient &&
                        decision.Message.MessageId != 0;
                    if (quoting && messages.Count > 0) messages[0].Insert(0, OneBotSegment.Reply(decision.Message.MessageId));
                }
                if (!live)
                {
                    succeeded = true;
                    return new("preview", reply.Text, memeId, ImageUrl: imageBytes is null ? null : reply.ImageUrl, Voice: voiceWav is not null);
                }
                if (await client.GetLoginInfoAsync(work.Token).ConfigureAwait(false) != expectedAccount)
                {
                    policy.SetPaused(true);
                    return new("account_changed_paused");
                }
                work.Token.ThrowIfCancellationRequested();
                if (!policy.IsCurrent(id)) return new("stale");
                var account = decision.Message.SenderId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var groupAccount = decision.Message.GroupId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                OneBotMessageReceipt? receipt = null;
                if (voiceWav is not null)
                {
                    receipt = decision.Message.IsPrivate
                        ? await client.SendPrivateAsync(new(account, [OneBotSegment.Record(voiceWav)]), work.Token).ConfigureAwait(false)
                        : await client.SendGroupAsync(new(groupAccount, [OneBotSegment.Record(voiceWav)]), work.Token).ConfigureAwait(false);
                }
                else
                {
                    for (var index = 0; index < messages!.Count; index++)
                    {
                        if (index > 0)
                        {
                            await Task.Delay(SegmentDelay, work.Token).ConfigureAwait(false);
                            if (!policy.IsCurrent(id)) break; // 已失效就停在半途，不补发、不重发。
                        }
                        receipt = decision.Message.IsPrivate
                            ? await client.SendPrivateAsync(new(account, messages[index]), work.Token).ConfigureAwait(false)
                            : await client.SendGroupAsync(new(groupAccount, messages[index]), work.Token).ConfigureAwait(false);
                    }
                }
                succeeded = true;
                if (memeId is not null)
                {
                    if (!recentMemes.ContainsKey(session) && recentMemes.Count >= MaximumRecentMemeSessions)
                        recentMemes.Remove(recentMemes.MinBy(item => item.Value.At).Key);
                    recentMemes[session] = (memeId, clock?.Invoke() ?? DateTimeOffset.UtcNow);
                }
                var spoken = reply.Text + (memeId is null ? "" : " [表情包:" + memeId + "]") +
                    (imageBytes is null ? "" : " [图片]") + (voiceWav is null ? "" : " [语音]");
                if (decision.Message.IsPrivate) policy.RecordOwnPrivateReply(decision.Message.SenderId, spoken);
                else policy.RecordOwnReply(decision.Message.GroupId, spoken);
                confirmedReply?.Invoke(decision.Message, reply.Text.Length > 0 ? reply.Text : spoken, receipt?.MessageId ?? "");
                return new("api_accepted", reply.Text, memeId, receipt?.MessageId,
                    imageBytes is null ? null : reply.ImageUrl, voiceWav is not null);
            }
            finally { sendGate.Release(); }
        }
        finally
        {
            policy.Complete(id, succeeded);
            lock (gate) if (active == work) active = null;
        }
    }

    /// <summary>网络图片/本机合成语音的失败只降级为不发，不让整轮文字回复失败。</summary>
    private static async Task<byte[]?> TryAsync(Func<Task<byte[]>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException or HttpRequestException or TimeoutException or NotSupportedException or InvalidOperationException)
        { return null; }
    }

    private static readonly TimeSpan SegmentDelay = TimeSpan.FromMilliseconds(500);

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
