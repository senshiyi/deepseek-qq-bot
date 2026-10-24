using System.Buffers.Binary;
using NAudio.Wave;

namespace AudioBridge;

public static class TestTone
{
    public static PcmQueue Create(double volume, int milliseconds)
    {
        new BridgeConfig { Volume = volume, ToneMilliseconds = milliseconds }.Validate(false);
        var format = new WaveFormat(48000, 16, 1);
        var source = new PcmQueue(format, milliseconds + 20);
        var id = source.Reset();
        var frames = format.SampleRate * milliseconds / 1000;
        var bytes = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            // 440 Hz with a 10 ms edge ramp; 100 ms silence at each end is unnecessary for the probe.
            var envelope = Math.Clamp(Math.Min(i, frames - 1 - i) / 480d, 0, 1);
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / format.SampleRate) * volume * envelope * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), value);
        }
        if (!source.TryEnqueue(id, bytes)) throw new InvalidOperationException("测试音超过缓冲上限。");
        source.Complete(id);
        return source;
    }
}
