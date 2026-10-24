using System.Buffers.Binary;
using AudioBridge;
using NAudio.Wave;

internal static class WavDiagnosticsTests
{
    private static readonly BridgeConfig Config = new() { OutputDeviceId = "fake-render", Volume = 0.2 };
    private static TaskCompletionSource Ready() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task InvalidWavs()
    {
        var malformed = NewPath();
        File.WriteAllText(malformed, "not a RIFF/WAVE file");
        var empty = Wave(new WaveFormat(8000, 16, 1), []);
        var overlong = Wave(new WaveFormat(8000, 16, 1), new byte[31 * 8000 * 2]);
        var compressed = Wave(WaveFormat.CreateCustomFormat(WaveFormatEncoding.MuLaw, 8000, 1, 8000, 1, 8), new byte[800]);
        var wrongExtension = Wave(new WaveFormat(8000, 16, 1), new byte[1600], ".bin");
        var calls = 0;
        Task Player(string _, IWaveProvider source, CancellationToken ct) { calls++; return Task.CompletedTask; }
        try
        {
            foreach (var path in new[] { malformed, empty, overlong, compressed, wrongExtension, NewPath() })
            {
                var rejected = false;
                try { await DiagnosticsCommands.WavAsync(Config, "simulated", path, Player, default); }
                catch (Exception error) when (error is ArgumentException or InvalidDataException or
                    FormatException or NotSupportedException or FileNotFoundException) { rejected = true; }
                Check(rejected, $"invalid WAV accepted: {Path.GetFileName(path)}");
            }
            Check(calls == 0, "a rejected WAV reached the playback callback");
        }
        finally { foreach (var path in new[] { malformed, empty, overlong, compressed, wrongExtension }) File.Delete(path); }
    }

    public static async Task GainAndCompletion()
    {
        var path = Wave(new WaveFormat(8000, 16, 1), [0, 64, 0, 64, 0, 64, 0, 64]);
        var entered = Ready(); var release = Ready(); var calls = 0;
        async Task Player(string id, IWaveProvider source, CancellationToken ct)
        {
            calls++;
            Check(id == Config.OutputDeviceId, "WAV changed the selected output");
            var data = new byte[16];
            Check(source.Read(data) == 16 && source.Read(data) == 0, "WAV payload length or EOF changed");
            Check(Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(data) - 0.1f) < 0.00001f, "configured gain was not applied");
            entered.SetResult();
            await release.Task;
        }
        try
        {
            var operation = DiagnosticsCommands.WavAsync(Config, "simulated", path, Player, default);
            await entered.Task;
            Check(!operation.IsCompleted, "WAV returned before the player completed");
            release.SetResult();
            await operation;
            Check(calls == 1, "WAV repeated playback");
            using var reopened = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        finally { release.TrySetResult(); File.Delete(path); }
    }

    public static async Task ValidFormatsAndBoundary()
    {
        var boundary = Wave(new WaveFormat(8000, 16, 1), new byte[30 * 8000 * 2]);
        var ieee = Wave(WaveFormat.CreateIeeeFloatWaveFormat(8000, 1), [0, 0, 0, 63]);
        var calls = 0;
        Task Player(string _, IWaveProvider source, CancellationToken ct) { calls++; return Task.CompletedTask; }
        try
        {
            await DiagnosticsCommands.WavAsync(Config, "simulated", boundary, Player, default);
            await DiagnosticsCommands.WavAsync(Config, "simulated", ieee, Player, default);
            Check(calls == 2, "IEEE or exactly 30 second WAV was rejected");
        }
        finally { File.Delete(boundary); File.Delete(ieee); }
    }

    public static async Task Cancellation()
    {
        using var stopping = new CancellationTokenSource();
        var entered = Ready(); var cancelled = Ready(); var release = Ready();
        var owner = new Owner();
        var operation = DiagnosticsCommands.PlayOnceAsync("wav", new { simulated = true }, TimeSpan.FromSeconds(1), async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(System.Threading.Timeout.Infinite, ct); }
            finally { cancelled.SetResult(); await release.Task; }
        }, stopping.Token, owner);
        await entered.Task;
        stopping.Cancel();
        await cancelled.Task;
        Check(!operation.IsCompleted && !owner.Disposed, "cancel returned or disposed the source before player cleanup");
        release.SetResult();
        try { await operation; throw new Exception("cancellation was swallowed"); }
        catch (OperationCanceledException) { }
        Check(owner.Disposed, "source owner leaked after cancellation");
    }

    public static async Task Timeout()
    {
        var entered = Ready(); var cancelled = Ready();
        var operation = DiagnosticsCommands.PlayOnceAsync("wav", new { simulated = true }, TimeSpan.FromMilliseconds(25), async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(System.Threading.Timeout.Infinite, ct); }
            finally { cancelled.SetResult(); }
        }, default);
        await entered.Task;
        try { await operation; throw new Exception("deadline was swallowed"); }
        catch (TimeoutException) { }
        Check(cancelled.Task.IsCompleted, "deadline did not cancel and wait for the player");
    }

    public static async Task CancelledBeforeSubmission()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        var owner = new Owner(); var calls = 0;
        try
        {
            await DiagnosticsCommands.PlayOnceAsync("wav", new { simulated = true }, TimeSpan.FromSeconds(1),
                _ => { calls++; return Task.CompletedTask; }, stopping.Token, owner);
            throw new Exception("cancelled single playback started");
        }
        catch (OperationCanceledException) { }
        Check(calls == 0 && owner.Disposed, "cancelled submission played or leaked its prepared source");
    }

    private static string NewPath(string extension = ".wav")
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Cache/audio-fixtures"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "wav-validation-" + Guid.NewGuid().ToString("N") + extension);
    }

    private static string Wave(WaveFormat format, byte[] data, string extension = ".wav")
    {
        var path = NewPath(extension);
        using var writer = new WaveFileWriter(path, format);
        writer.Write(data, 0, data.Length);
        return path;
    }

    private sealed class Owner : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
