using System.Buffers.Binary;
using NAudio.Wave;

namespace AudioBridge;

public sealed class SignalMeter
{
    private readonly object gate = new();
    private long samples;
    private double squareSum;
    private double peak;
    private DateTimeOffset? lastSignal;
    private string format = "尚未收到数据";

    public void Add(ReadOnlySpan<byte> data, WaveFormat waveFormat)
    {
        var encoding = waveFormat is WaveFormatExtensible ext ? ext.ToStandardWaveFormat().Encoding : waveFormat.Encoding;
        var bytesPerSample = waveFormat.BitsPerSample / 8;
        if (bytesPerSample is not (2 or 4) || (encoding != WaveFormatEncoding.Pcm && encoding != WaveFormatEncoding.IeeeFloat))
            throw new NotSupportedException($"不支持的采集格式：{waveFormat}");
        double sum = 0, max = 0;
        var count = data.Length / bytesPerSample;
        for (var i = 0; i < count; i++)
        {
            var value = encoding == WaveFormatEncoding.IeeeFloat
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..]))
                : bytesPerSample == 2
                    ? BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]) / 32768d
                    : BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..]) / 2147483648d;
            if (!double.IsFinite(value)) continue;
            sum += value * value;
            max = Math.Max(max, Math.Abs(value));
        }
        lock (gate)
        {
            samples += count;
            squareSum += sum;
            peak = Math.Max(peak, max);
            format = waveFormat.ToString();
            if (max > 0.01) lastSignal = DateTimeOffset.Now;
        }
    }

    public object Snapshot()
    {
        lock (gate)
        {
            return new { samples, rms = samples == 0 ? 0 : Math.Sqrt(squareSum / samples), peak, lastSignal, format };
        }
    }

    public void Clear() { lock (gate) { samples = 0; squareSum = peak = 0; lastSignal = null; } }
}
