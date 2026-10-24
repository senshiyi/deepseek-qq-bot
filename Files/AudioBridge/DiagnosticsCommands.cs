using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

[assembly: InternalsVisibleTo("AudioBridge.Tests")]

namespace AudioBridge;

public static class DiagnosticsCommands
{
    public static async Task ProbeAsync(BridgeConfig config, string testGroup, double seconds,
        double? toneAtSeconds, CancellationToken ct)
    {
        if (!double.IsFinite(seconds) || seconds is < 1 or > 30)
            throw new ArgumentException("seconds 必须为 1～30 秒。");
        if (toneAtSeconds is { } at && (!double.IsFinite(at) || at < 0 ||
            at > seconds - config.ToneMilliseconds / 1000d))
            throw new ArgumentException("toneAtSeconds 必须从 0 开始，且为测试音留出完整时长。");
        if (toneAtSeconds.HasValue) config.Validate(false);
        ct.ThrowIfCancellationRequested();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        session.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var output = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        var meter = new SignalMeter();
        var watch = Stopwatch.StartNew();
        Write("probe", "requested", new { testGroup, config.QqProcessId, durationSeconds = seconds, toneAtSeconds });

        var capture = Task.Run(async () =>
        {
            try
            {
                await NativeAudio.CaptureProcessAsync(config.QqProcessId,
                    (data, format) => { if (!session.IsCancellationRequested) meter.Add(data.Span, format); },
                    session.Token).ConfigureAwait(false);
            }
            finally { output.Cancel(); }
        });
        async Task ScheduledToneAsync()
        {
            if (toneAtSeconds is not { } start) return;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(start), output.Token).ConfigureAwait(false);
                output.Token.ThrowIfCancellationRequested();
                await ToneAsync(config, testGroup, output.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (output.IsCancellationRequested) { }
            catch { session.Cancel(); throw; }
        }
        try
        {
            await Task.WhenAll(capture, ScheduledToneAsync()).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Write("probe", "completed", new { durationSeconds = seconds, elapsedSeconds = watch.Elapsed.TotalSeconds,
                cleanupConfirmed = true, meter = meter.Snapshot() });
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Write("probe", "completed", new { durationSeconds = seconds, elapsedSeconds = watch.Elapsed.TotalSeconds,
                cleanupConfirmed = true, meter = meter.Snapshot() });
        }
        catch (Exception error)
        {
            session.Cancel();
            output.Cancel();
            Write("probe", error is TimeoutException ? "timeout" : "error", new { durationSeconds = seconds,
                elapsedSeconds = watch.Elapsed.TotalSeconds, cleanupConfirmed = false,
                error = error.Message, meter = meter.Snapshot() });
            throw;
        }
    }

    public static Task ToneAsync(BridgeConfig config, string testGroup, CancellationToken ct)
    {
        config.Validate(false);
        return PlayOnceAsync("tone", new { testGroup, config.OutputDeviceId, config.Volume,
            durationMilliseconds = config.ToneMilliseconds, remoteAudibility = "unverified" },
            TimeSpan.FromMilliseconds(config.ToneMilliseconds + 5000),
            token => NativeAudio.PlayAsync(config.OutputDeviceId!, TestTone.Create(config.Volume, config.ToneMilliseconds), token), ct);
    }

    public static Task WavAsync(BridgeConfig config, string testGroup, string wavPath, CancellationToken ct) =>
        WavAsync(config, testGroup, wavPath, NativeAudio.PlayAsync, ct);

    internal static async Task WavAsync(BridgeConfig config, string testGroup, string wavPath,
        Func<string, IWaveProvider, CancellationToken, Task> player, CancellationToken ct)
    {
        config.Validate(false);
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        if (!string.Equals(Path.GetExtension(wavPath), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("固定音频必须是 .wav 文件。");
        WaveFileReader? reader = new(Path.GetFullPath(wavPath));
        try
        {
            var format = reader.WaveFormat is WaveFormatExtensible ext ? ext.ToStandardWaveFormat() : reader.WaveFormat;
            if (format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat) ||
                (format.Encoding == WaveFormatEncoding.Pcm ? format.BitsPerSample is not (8 or 16 or 24 or 32) :
                    format.BitsPerSample is not (32 or 64)))
                throw new NotSupportedException("仅支持 PCM 8/16/24/32 位或 IEEE float 32/64 位 WAV。");
            var expectedAlign = (long)format.Channels * (format.BitsPerSample / 8);
            var expectedRate = (long)format.SampleRate * expectedAlign;
            if (format.Channels <= 0 || format.SampleRate <= 0 || expectedAlign <= 0 ||
                format.BlockAlign != expectedAlign || format.AverageBytesPerSecond != expectedRate ||
                reader.Length <= 0 || reader.Length % expectedAlign != 0)
                throw new InvalidDataException("WAV 格式、帧对齐或数据长度无效。");
            var duration = TimeSpan.FromSeconds(reader.Length / (double)expectedRate);
            if (reader.Length > expectedRate * 30)
                throw new ArgumentException("固定 WAV 最长为 30 秒。");
            var provider = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = (float)config.Volume };
            var request = new { testGroup, config.OutputDeviceId, config.Volume, wavPath = Path.GetFullPath(wavPath),
                durationSeconds = duration.TotalSeconds, format = reader.WaveFormat.ToString(), remoteAudibility = "unverified" };
            var owner = reader;
            reader = null; // The background playback owns the same prevalidated file until cleanup ends.
            await PlayOnceAsync("wav", request, duration + TimeSpan.FromSeconds(5),
                token => player(config.OutputDeviceId!, provider.ToWaveProvider(), token), ct, owner).ConfigureAwait(false);
        }
        finally { reader?.Dispose(); }
    }

    internal static async Task PlayOnceAsync(string command, object request, TimeSpan limit,
        Func<CancellationToken, Task> play, CancellationToken ct, IDisposable? sourceOwner = null)
    {
        var submitted = false;
        var playing = Task.CompletedTask;
        var watch = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = stopping.Token;
            Write(command, "requested", request);
            playing = Task.Run(async () =>
            {
                using (sourceOwner)
                {
                    token.ThrowIfCancellationRequested();
                    await play(token).ConfigureAwait(false);
                }
            });
            submitted = true;
            try
            {
                await WaitForPlaybackAsync(playing, stopping, limit, ct).ConfigureAwait(false);
                Write(command, "completed", new { elapsedSeconds = watch.Elapsed.TotalSeconds,
                    cleanupConfirmed = true, localPlaybackCompleted = true, remoteAudibility = "unverified" });
            }
            catch (Exception error)
            {
                Write(command, error is TimeoutException ? "timeout" : "error", new {
                    elapsedSeconds = watch.Elapsed.TotalSeconds,
                    cleanupConfirmed = playing.IsCompletedSuccessfully || playing.IsCanceled,
                    error = error.Message, remoteAudibility = "unverified" });
                throw;
            }
        }
        finally { if (!submitted) sourceOwner?.Dispose(); }
    }

    private static async Task WaitForPlaybackAsync(Task playing, CancellationTokenSource stopping,
        TimeSpan limit, CancellationToken ct)
    {
        try { await playing.WaitAsync(limit, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            stopping.Cancel();
            try { await playing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (TimeoutException timeout) when (!playing.IsCompleted)
            {
                Observe(playing);
                throw new TimeoutException("本地播放在 5 秒内未确认退出；后台仍在清理，资源尚未确认释放。", timeout);
            }
            throw;
        }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(faulted => _ = faulted.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static void Write(string command, string status, object details) => Console.WriteLine(
        JsonSerializer.Serialize(new { command, status, utc = DateTimeOffset.UtcNow,
            local = DateTimeOffset.Now, details }, BridgeConfig.JsonOptions));
}
