using System.Buffers.Binary;
using NAudio.Wave;

namespace AudioBridge;

public sealed record AudioFrame(float[] Samples, int SampleRate, long Sequence, long ReceivedTimestamp)
{
    public double Rms => Samples.Length == 0 ? 0 : Math.Sqrt(Samples.Sum(x => (double)x * x) / Samples.Length);
}

public static class AudioFrames
{
    // Caller copies callback-owned data here; sherpa handles rate conversion after downmixing.
    public static float[] ToMono(ReadOnlySpan<byte> bytes, WaveFormat source)
    {
        var format = source is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : source;
        var width = format.BitsPerSample / 8;
        if (format.Channels <= 0 || format.BlockAlign != width * format.Channels ||
            bytes.Length % format.BlockAlign != 0 ||
            (format.Encoding == WaveFormatEncoding.IeeeFloat ? width != 4 :
                format.Encoding != WaveFormatEncoding.Pcm || width is not (2 or 3 or 4)))
            throw new NotSupportedException($"不支持本地识别的输入格式：{format}");
        var mono = new float[bytes.Length / format.BlockAlign];
        for (var frame = 0; frame < mono.Length; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = frame * format.BlockAlign + channel * width;
                var data = bytes[offset..];
                var value = format.Encoding == WaveFormatEncoding.IeeeFloat
                    ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data))
                    : width == 2 ? BinaryPrimitives.ReadInt16LittleEndian(data) / 32768f
                    : width == 3 ? ((data[0] | data[1] << 8 | data[2] << 16) << 8 >> 8) / 8388608f
                    : BinaryPrimitives.ReadInt32LittleEndian(data) / 2147483648f;
                if (float.IsFinite(value)) sum += Math.Clamp(value, -1, 1);
            }
            mono[frame] = (float)(sum / format.Channels);
        }
        return mono;
    }
}
