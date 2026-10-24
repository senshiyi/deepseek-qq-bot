using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;

namespace AudioBridge;

public static class AsrFileCommand
{
    public static Task RunAsync(BotSettings settings, string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("asr-file只读取明确指定的WAV测试文件。");
        using var reader = new WaveFileReader(Path.GetFullPath(path));
        if (reader.TotalTime.TotalSeconds is <= 0 or > 30) throw new ArgumentException("ASR文件测试限制为0～30秒。");
        var format = reader.WaveFormat;
        var frameSize = Math.Max(format.BlockAlign, format.AverageBytesPerSecond / 50 / format.BlockAlign * format.BlockAlign);
        var buffer = new byte[frameSize];
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        using var recognizer = new LocalRecognizer(Path.GetFullPath(settings.ModelDirectory));
        var loadedMs = watch.Elapsed.TotalMilliseconds;
        var text = new List<string>();
        int read;
        while ((read = reader.Read(buffer)) > 0)
            foreach (var update in recognizer.Accept(AudioFrames.ToMono(buffer.AsSpan(0, read), format), format.SampleRate))
                if (update.IsFinal) text.Add(update.Text);
        for (var i = 0; i < 80; i++)
            foreach (var update in recognizer.Accept(new float[format.SampleRate / 50], format.SampleRate))
                if (update.IsFinal) text.Add(update.Text);
        foreach (var update in recognizer.Flush()) if (update.IsFinal) text.Add(update.Text);
        process.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new { command = "asr-file", local = DateTimeOffset.Now,
            path = Path.GetFullPath(path), text, modelLoadMilliseconds = loadedMs,
            totalWallMilliseconds = watch.Elapsed.TotalMilliseconds,
            processCpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            privateMemoryMiB = process.PrivateMemorySize64 / 1048576d,
            sourceDurationSeconds = reader.TotalTime.TotalSeconds,
            scope = "Local fixed-file self-test, no playback/upload/QQ capture; not real-time group latency." }, BridgeConfig.JsonOptions));
        return Task.CompletedTask;
    }
}
