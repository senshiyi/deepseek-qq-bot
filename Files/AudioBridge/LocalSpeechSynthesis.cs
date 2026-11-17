using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace AudioBridge;

/// <summary>
/// 本机 SAPI 合成，输出 16kHz/16位/单声道 PCM。取消不阻塞调用方，也不在原生调用上等待。
/// WindowsVoiceSpeaker 与 QQ 语音共用这一份实现。
/// </summary>
internal static class LocalSpeechSynthesis
{
    public const int SampleRate = 16000;

    public static async Task<byte[]> SynthesizePcmAsync(string text, string voiceName, int rate, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceName);
        using var output = new MemoryStream();
        var synthesizer = new SpeechSynthesizer();
        var access = new object();
        var disposed = false;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Observe(complete.Task);
        void OnCompleted(object? sender, SpeakCompletedEventArgs args)
        {
            if (args.Error is not null) complete.TrySetException(args.Error);
            else if (args.Cancelled) complete.TrySetCanceled();
            else complete.TrySetResult();
        }
        synthesizer.SpeakCompleted += OnCompleted;
        var cancellation = ct.Register(() =>
        {
            // Do not block the caller's cancellation on a SAPI/native call or a held startup lock.
            _ = Task.Run(() =>
            {
                try { lock (access) { if (!disposed) synthesizer.SpeakAsyncCancelAll(); } }
                catch (Exception error) { complete.TrySetException(error); }
            });
        });
        try
        {
            lock (access)
            {
                ct.ThrowIfCancellationRequested();
                synthesizer.SelectVoice(voiceName);
                synthesizer.Rate = rate;
                synthesizer.Volume = 100;
                synthesizer.SetOutputToAudioStream(output, new SpeechAudioFormatInfo(SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                ct.ThrowIfCancellationRequested();
                synthesizer.SpeakAsync(text);
            }
            await complete.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return output.ToArray();
        }
        finally
        {
            cancellation.Dispose();
            lock (access)
            {
                disposed = true;
                synthesizer.SpeakCompleted -= OnCompleted;
                synthesizer.Dispose();
            }
        }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(faulted => _ = faulted.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
