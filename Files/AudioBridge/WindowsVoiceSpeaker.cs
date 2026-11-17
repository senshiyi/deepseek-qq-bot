using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioBridge;

public sealed class WindowsVoiceSpeaker : IVoiceSpeaker, IAsyncDisposable
{
    private readonly string outputDeviceId;
    private readonly double volume;
    private readonly int rate;
    private readonly string voiceName;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string, CancellationToken, Task<byte[]>> synthesize;
    private readonly Func<string, IWaveProvider, CancellationToken, Task> play;
    private readonly TimeSpan cleanupTimeout = TimeSpan.FromSeconds(5);
    private Task? abandoned;
    private int cleanupUnconfirmed;
    private int disposing;

    /// <summary>Raised once per Speak when its first nonzero PCM is read by the local playback provider.</summary>
    public event Action? PlaybackStarted;

    public WindowsVoiceSpeaker(string outputDeviceId, double volume, int rate = 0,
        string voiceName = "Microsoft Huihui Desktop")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceName);
        if (!double.IsFinite(volume) || volume is < 0 or > 0.5)
            throw new ArgumentOutOfRangeException(nameof(volume), "语音音量必须为 0～0.5。");
        if (rate is < -3 or > 3) throw new ArgumentOutOfRangeException(nameof(rate), "语速必须为 -3～3。");
        this.outputDeviceId = outputDeviceId;
        this.volume = volume;
        this.rate = rate;
        this.voiceName = voiceName;
        synthesize = SynthesizeCoreAsync;
        play = NativeAudio.PlayAsync;
    }

    internal WindowsVoiceSpeaker(string outputDeviceId, double volume,
        Func<string, CancellationToken, Task<byte[]>> synthesize,
        Func<string, IWaveProvider, CancellationToken, Task> play, TimeSpan? cleanupTimeout = null)
        : this(outputDeviceId, volume)
    {
        this.synthesize = synthesize;
        this.play = play;
        this.cleanupTimeout = cleanupTimeout ?? this.cleanupTimeout;
    }

    public Task SpeakAsync(string text, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return SerializedAsync(async token =>
        {
            var notified = 0;
            foreach (var sentence in SplitSentences(text))
            {
                var pcm = await SynthesizePcmAsync(sentence, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await RunStageAsync(async playbackToken =>
                {
                    using var stream = new RawSourceWaveStream(new MemoryStream(pcm, writable: false), new WaveFormat(16000, 16, 1));
                    var gain = new VolumeSampleProvider(stream.ToSampleProvider()) { Volume = (float)volume };
                    var provider = new FirstPcmProvider(gain.ToWaveProvider(), () =>
                    {
                        if (Interlocked.CompareExchange(ref notified, 1, 0) == 0) PlaybackStarted?.Invoke();
                    }, playbackToken);
                    await play(outputDeviceId, provider, playbackToken).ConfigureAwait(false);
                    return true;
                }, TimeSpan.FromSeconds(pcm.Length / 32000d + 5), "播放", token).ConfigureAwait(false);
            }
            return true;
        }, ct);
    }

    internal Task<byte[]> SynthesizeForCheckAsync(string text, CancellationToken ct) =>
        SerializedAsync(token => SynthesizePcmAsync(text, token), ct);

    private async Task<byte[]> SynthesizePcmAsync(string text, CancellationToken ct)
    {
        var pcm = await RunStageAsync(token => synthesize(text, token), TimeSpan.FromSeconds(15), "本地合成", ct).ConfigureAwait(false);
        if (pcm.Length == 0 || pcm.Length % 2 != 0 || pcm.Length > 32000 * 30)
            throw new InvalidDataException("本地语音未产生有效的 16kHz/16位单声道短句 PCM（最长30秒）。");
        return pcm;
    }

    private async Task<T> SerializedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposing) != 0, this);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await serial.WaitAsync(stopping.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposing) != 0, this);
            if (Volatile.Read(ref cleanupUnconfirmed) != 0)
                throw new InvalidOperationException("此前语音清理超时，资源未确认释放；此实例禁止再次发声。");
            return await operation(stopping.Token).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }

    private async Task<T> RunStageAsync<T>(Func<CancellationToken, Task<T>> operation, TimeSpan limit,
        string stage, CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stopping.Token;
        var running = Task.Run(async () => { token.ThrowIfCancellationRequested(); return await operation(token).ConfigureAwait(false); });
        try { return await running.WaitAsync(limit, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            stopping.Cancel();
            try { await running.WaitAsync(cleanupTimeout).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (TimeoutException timeout) when (!running.IsCompleted)
            {
                Volatile.Write(ref abandoned, running);
                Interlocked.Exchange(ref cleanupUnconfirmed, 1);
                Observe(running);
                throw new TimeoutException($"{stage}在清理期限内未退出；资源尚未确认释放，此实例已锁定。", timeout);
            }
            throw;
        }
    }

    /// <summary>SAPI 合成本机 PCM；具体实现在 LocalSpeechSynthesis，与 QQ 语音共用。</summary>
    private Task<byte[]> SynthesizeCoreAsync(string text, CancellationToken ct) =>
        LocalSpeechSynthesis.SynthesizePcmAsync(text, voiceName, rate, ct);

    internal static IEnumerable<string> SplitSentences(string text)
    {
        for (var start = 0; start < text.Length;)
        {
            var end = Math.Min(start + 40, text.Length);
            for (var index = start; index < end; index++)
                if ("。！？!?；;\n".Contains(text[index])) { end = index + 1; break; }
            if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
            var sentence = text[start..end].Trim();
            if (sentence.Length != 0) yield return sentence;
            start = end;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref disposing, 1, 0) == 0) _ = CompleteDisposalAsync();
        return new ValueTask(disposal.Task);
    }

    private async Task CompleteDisposalAsync()
    {
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            var cleanup = Task.Run(async () =>
            {
                await serial.WaitAsync().ConfigureAwait(false);
                try
                {
                    var pending = Volatile.Read(ref abandoned);
                    if (pending is not null) { try { await pending.ConfigureAwait(false); } catch { /* already reported and observed */ } }
                }
                finally { serial.Dispose(); lifetime.Dispose(); }
            });
            try { await cleanup.WaitAsync(cleanupTimeout + TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException timeout)
            {
                Interlocked.Exchange(ref cleanupUnconfirmed, 1);
                Observe(cleanup);
                throw new TimeoutException("语音实例退出时底层清理仍未完成；资源尚未确认释放，实例已停用。", timeout);
            }
            disposal.TrySetResult();
        }
        catch (Exception error) { disposal.TrySetException(error); }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(faulted => _ = faulted.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private sealed class FirstPcmProvider(IWaveProvider source, Action firstPcm, CancellationToken ct) : IWaveProvider
    {
        private int notified;
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(Span<byte> buffer)
        {
            if (ct.IsCancellationRequested) { buffer.Clear(); return 0; }
            var count = source.Read(buffer);
            if (ct.IsCancellationRequested) { buffer.Clear(); return 0; }
            if (Volatile.Read(ref notified) == 0)
                foreach (var value in MemoryMarshal.Cast<byte, float>(buffer[..count]))
                    if (float.IsFinite(value) && value != 0)
                    {
                        if (Interlocked.Exchange(ref notified, 1) == 0) firstPcm();
                        break;
                    }
            return count;
        }
    }
}
