using System.Text;
using System.Threading.Channels;

namespace AudioBridge;

/// <summary>
/// Owns conversation work, not the injected provider or speaker. SubmitAsync starts a reply;
/// WaitForIdleAsync waits for speech, never an abandoned network iterator. Interrupt/replace
/// wait for the old SpeakAsync to exit, so an uncooperative speaker cannot overlap new audio.
/// </summary>
public sealed class VoiceConversation : IAsyncDisposable
{
    public const int MaximumRounds = 8;
    public const int MaximumHistoryCharacters = 4000;
    public const int MaximumReplyCharacters = 180;
    public const int MaximumReplySentences = 3;
    private const int SentenceCharacters = 60;
    private const string InterruptionNote = "上一轮回复已打断；历史仅保留完整播放的短句，未播完部分已丢弃。";
    private readonly IStreamingReply reply;
    private readonly IVoiceSpeaker speaker;
    private readonly SemaphoreSlim transition = new(1, 1);
    private readonly object gate = new();
    private readonly List<Round> rounds = [];
    private Session? current;
    private long generation;
    private bool muted, disposed, speaking;
    private string? lastError;
    private TimeoutException? cleanupFailure;

    public VoiceConversation(IStreamingReply reply, IVoiceSpeaker speaker)
    {
        this.reply = reply ?? throw new ArgumentNullException(nameof(reply));
        this.speaker = speaker ?? throw new ArgumentNullException(nameof(speaker));
    }

    public async Task<long> SubmitAsync(string userText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        // Reserve room for spoken output and the interruption annotation in the newest round.
        userText = LimitText(userText.Trim(), 3600);
        await transition.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
            await StopCurrentAsync().ConfigureAwait(false);
            lock (gate)
            {
                var id = ++generation;
                if (muted) return id;
                lastError = null;
                var round = new Round(userText);
                rounds.Add(round);
                TrimHistory();
                var session = new Session(id, round);
                current = session;
                var history = History();
                session.Output = Task.Run(() => ConsumeAsync(session));
                ObserveFault(session.Output);
                // A provider may block synchronously or ignore ct: it never owns the transition.
                _ = Task.Run(() => ProduceAsync(session, history));
                return id;
            }
        }
        finally { transition.Release(); }
    }

    public async Task InterruptAsync()
    {
        await transition.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
            await StopCurrentAsync().ConfigureAwait(false);
        }
        finally { transition.Release(); }
    }

    public async Task SetMutedAsync(bool value)
    {
        await transition.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                ThrowIfCleanupFailed();
                muted = value;
            }
            if (value) await StopCurrentAsync().ConfigureAwait(false);
        }
        finally { transition.Release(); }
    }

    public VoiceConversationSnapshot Snapshot()
    {
        lock (gate)
            return new(generation, current is { Finished: false, ProducerFinished: false } &&
                !current.Token.IsCancellationRequested, speaking, muted,
                rounds.Count > 0 && rounds[^1].Interrupted, disposed,
                current?.Sentences.Reader.Count ?? 0, lastError, History());
    }

    public Task WaitForIdleAsync(CancellationToken ct = default)
    {
        Task output;
        lock (gate) output = cleanupFailure is null
            ? current?.Output ?? Task.CompletedTask
            : Task.FromException(CleanupFailure());
        return output.WaitAsync(ct);
    }

    private async Task StopCurrentAsync()
    {
        Session? previous;
        lock (gate)
        {
            ++generation; // Fence before cancellation, including providers that ignore their token.
            previous = current;
            current = null;
            if (previous is not null && !previous.Finished)
            {
                previous.Round.Interrupted = true;
                TrimHistory();
            }
        }
        if (previous is null)
        {
            lock (gate) ThrowIfCleanupFailed();
            return;
        }
        // Independent sources keep a network provider's blocking cancellation callback away
        // from speech cancellation. Never wait for those callbacks or the network iterator.
        CancelAndDispose(previous.SpeechCancellation);
        CancelAndDispose(previous.ProviderCancellation);
        previous.Sentences.Writer.TryComplete();
        while (previous.Sentences.Reader.TryRead(out _)) { }
        await previous.Output.ConfigureAwait(false);
        lock (gate) ThrowIfCleanupFailed();
    }

    private async Task ProduceAsync(Session session, IReadOnlyList<ChatTurn> history)
    {
        var pending = new StringBuilder();
        var characters = 0;
        var sentenceCharacters = 0;
        var sentenceCount = 0;
        bool Emit()
        {
            var text = pending.ToString().Trim();
            pending.Clear();
            sentenceCharacters = 0;
            if (text.Length == 0 || !text.EnumerateRunes().Any(Rune.IsLetterOrDigit)) return true;
            lock (gate)
            {
                if (!IsCurrent(session) || !session.Sentences.Writer.TryWrite(text)) return false;
            }
            return ++sentenceCount < MaximumReplySentences && characters < MaximumReplyCharacters;
        }
        void FinishOutput()
        {
            session.Sentences.Writer.TryComplete();
            _ = RequestCancellation(session.ProviderCancellation);
        }
        try
        {
            await foreach (var chunk in reply.StreamAsync(history, session.ProviderToken)
                .WithCancellation(session.ProviderToken).ConfigureAwait(false))
            {
                lock (gate) if (!IsCurrent(session)) return;
                if (string.IsNullOrEmpty(chunk)) continue;
                foreach (var rune in chunk.EnumerateRunes())
                {
                    pending.Append(rune.ToString());
                    characters++;
                    sentenceCharacters++;
                    if (characters >= MaximumReplyCharacters || sentenceCharacters >= SentenceCharacters ||
                        rune.Value is '。' or '！' or '？' or '.' or '!' or '?' or '；' or ';' or '\n')
                    {
                        if (!Emit()) { FinishOutput(); return; }
                    }
                }
            }
            Emit();
        }
        catch (OperationCanceledException) when (session.ProviderToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (gate) if (IsCurrent(session)) lastError = error.Message;
            _ = RequestCancellation(session.SpeechCancellation);
        }
        finally
        {
            lock (gate) session.ProducerFinished = true;
            session.Sentences.Writer.TryComplete();
        }
    }

    private async Task ConsumeAsync(Session session)
    {
        var finished = false;
        try
        {
            await foreach (var text in session.Sentences.Reader.ReadAllAsync(session.Token).ConfigureAwait(false))
            {
                lock (gate)
                {
                    if (!IsCurrent(session)) return;
                    speaking = true;
                }
                try
                {
                    session.Token.ThrowIfCancellationRequested();
                    await speaker.SpeakAsync(text, session.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        // A return racing cancellation is conservatively excluded from history.
                        if (IsCurrent(session))
                        {
                            session.Round.Spoken.Add(text);
                            TrimHistory();
                        }
                    }
                }
                finally { lock (gate) speaking = false; }
            }
            finished = !session.Token.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (gate)
            {
                // A timed-out output owner may still be active after its Speak task exits.
                // Preserve that failure even when StopCurrent has already fenced the session.
                if (error is TimeoutException timeout)
                {
                    cleanupFailure ??= timeout;
                    lastError = timeout.Message;
                }
                else if (IsCurrent(session)) lastError = error.Message;
            }
            _ = RequestCancellation(session.SpeechCancellation);
            _ = RequestCancellation(session.ProviderCancellation);
            session.Sentences.Writer.TryComplete();
            while (session.Sentences.Reader.TryRead(out _)) { }
            if (error is TimeoutException) throw;
        }
        finally { lock (gate) session.Finished = finished; }
    }

    // Call while holding gate. IsCurrent fences both stream production and history commits.
    private bool IsCurrent(Session session) => !disposed && current == session &&
        generation == session.Id && !session.Token.IsCancellationRequested;

    // Call while holding gate. A new generation cannot make an abandoned output owner safe.
    private TimeoutException CleanupFailure() => new(
        "此前语音清理超时，资源尚未确认释放；本次会话禁止再次发声。", cleanupFailure);

    private void ThrowIfCleanupFailed()
    {
        if (cleanupFailure is not null) throw CleanupFailure();
    }

    private static void ObserveFault(Task task) => _ = task.ContinueWith(
        faulted => _ = faulted.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private ChatTurn[] History() => rounds.SelectMany(round =>
    {
        var turns = new List<ChatTurn> { new("user", round.User) };
        if (round.Spoken.Count > 0) turns.Add(new("assistant", string.Concat(round.Spoken)));
        if (round.Interrupted) turns.Add(new("system", InterruptionNote));
        return turns;
    }).ToArray();

    private void TrimHistory()
    {
        while (rounds.Count > MaximumRounds || History().Sum(turn => turn.Content.Length) > MaximumHistoryCharacters)
            rounds.RemoveAt(0);
    }

    private static string LimitText(string text, int limit)
    {
        if (text.Length <= limit) return text;
        const string suffix = "…（消息过长，已截断）";
        var length = limit - suffix.Length;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + suffix;
    }

    private static Task RequestCancellation(CancellationTokenSource source)
    {
        try
        {
            var cancellation = source.CancelAsync();
            _ = cancellation.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return cancellation;
        }
        catch (ObjectDisposedException) { return Task.CompletedTask; }
    }

    private static void CancelAndDispose(CancellationTokenSource source) =>
        _ = RequestCancellation(source).ContinueWith(task =>
        {
            _ = task.Exception;
            source.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public async ValueTask DisposeAsync()
    {
        await transition.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                if (disposed)
                {
                    ThrowIfCleanupFailed();
                    return;
                }
                disposed = true;
            }
            await StopCurrentAsync().ConfigureAwait(false);
        }
        finally { transition.Release(); }
    }

    private sealed class Round(string user)
    {
        public string User { get; } = user;
        public List<string> Spoken { get; } = [];
        public bool Interrupted { get; set; }
    }

    private sealed class Session
    {
        public Session(long id, Round round)
        {
            Id = id;
            Round = round;
            Token = SpeechCancellation.Token;
            ProviderToken = ProviderCancellation.Token;
        }
        public long Id { get; }
        public Round Round { get; }
        public CancellationTokenSource SpeechCancellation { get; } = new();
        public CancellationTokenSource ProviderCancellation { get; } = new();
        public CancellationToken Token { get; }
        public CancellationToken ProviderToken { get; }
        public Channel<string> Sentences { get; } = Channel.CreateBounded<string>(MaximumReplySentences);
        public Task Output { get; set; } = Task.CompletedTask;
        public bool Finished { get; set; }
        public bool ProducerFinished { get; set; }
    }
}
