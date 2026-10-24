using System.Diagnostics;
using AudioBridge;

public static class WindowsVoiceChecks
{
    // Uses only injected memory PCM and a fake player; no Windows audio endpoint is opened.
    public static async Task CancellationStopsPcm()
    {
        using var stopping = new CancellationTokenSource();
        var checkedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        await using var speaker = new WindowsVoiceSpeaker("unused-fake-endpoint", .1,
            (_, _) => Task.FromResult(new byte[] { 0, 64, 0, 64 }),
            (_, source, token) =>
            {
                stopping.Cancel();
                if (!token.IsCancellationRequested) throw new Exception("playback did not receive cancellation");
                var buffer = Enumerable.Repeat((byte)255, 16).ToArray();
                if (source.Read(buffer) != 0 || buffer.Any(value => value != 0))
                    throw new Exception("cancelled voice provider returned old PCM");
                checkedRead.SetResult();
                return Task.CompletedTask;
            });
        speaker.PlaybackStarted += () => Interlocked.Increment(ref notifications);
        try { await speaker.SpeakAsync("取消测试。", stopping.Token); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        await checkedRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (notifications != 0) throw new Exception("cancelled voice emitted a playback notification");
    }

    // Real Huihui synthesis is directed exclusively into a MemoryStream by SynthesizeForCheckAsync.
    public static async Task<object> LocalMemoryAndCancellation()
    {
        await using var speaker = new WindowsVoiceSpeaker("unused-memory-only-endpoint", .1);
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        var pcm = await speaker.SynthesizeForCheckAsync("你好，这是本地语音合成验证。声音只保存在内存中。", CancellationToken.None);
        var synthesisMs = timer.Elapsed.TotalMilliseconds;
        var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var peak = 0d;
        var square = 0d;
        var nonzero = 0;
        for (var index = 0; index < pcm.Length; index += 2)
        {
            var sample = BitConverter.ToInt16(pcm, index) / 32768d;
            peak = Math.Max(peak, Math.Abs(sample));
            square += sample * sample;
            if (sample != 0) nonzero++;
        }
        if (nonzero == 0) throw new Exception("Huihui returned silent PCM");

        using var stopping = new CancellationTokenSource();
        var longText = string.Concat(Enumerable.Repeat("这是用于验证本地合成取消的连续短句。", 400));
        var active = speaker.SynthesizeForCheckAsync(longText, stopping.Token);
        await Task.Delay(100);
        if (active.IsCompleted) throw new Exception("long synthesis completed before cancellation could be exercised");
        var cancelTimer = Stopwatch.StartNew();
        stopping.Cancel();
        var cancelled = false;
        try { await active; }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { cancelled = true; }
        if (!cancelled) throw new Exception("real synthesis ignored cancellation");
        var cancellationMs = cancelTimer.Elapsed.TotalMilliseconds;
        var recovered = await speaker.SynthesizeForCheckAsync("取消完成。", CancellationToken.None);
        return new
        {
            voice = "Microsoft Huihui Desktop",
            pcmBytes = pcm.Length,
            pcmSeconds = pcm.Length / 32000d,
            peak,
            rms = Math.Sqrt(square / (pcm.Length / 2)),
            nonzeroSamples = nonzero,
            synthesisMs,
            processCpuMs = cpuMs,
            cancellationRequestedAfterMs = 100,
            cancellationMs,
            recoveredPcmBytes = recovered.Length,
            playbackDevicesOpened = 0
        };
    }
}
