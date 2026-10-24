using NAudio.Wave;

namespace AudioBridge;

// Bounded PCM output with a session fence. No work is retained after cancellation.
public sealed class PcmQueue : IWaveProvider
{
    private readonly object gate = new();
    private readonly Queue<byte[]> chunks = new();
    private readonly int capacityBytes;
    private int offset;
    private int bufferedBytes;
    private long generation;
    private bool completed;
    public WaveFormat WaveFormat { get; }

    public PcmQueue(WaveFormat format, int capacityMilliseconds = 1000)
    {
        if (capacityMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(capacityMilliseconds));
        WaveFormat = format;
        capacityBytes = checked((int)((long)format.AverageBytesPerSecond * capacityMilliseconds / 1000));
    }

    public long Reset()
    {
        lock (gate)
        {
            generation++;
            chunks.Clear();
            offset = bufferedBytes = 0;
            completed = false;
            return generation;
        }
    }

    public bool TryEnqueue(long id, ReadOnlySpan<byte> pcm)
    {
        lock (gate)
        {
            if (id != generation || completed) return false;
            if (pcm.Length % WaveFormat.BlockAlign != 0) throw new ArgumentException("PCM 帧未对齐。");
            if (pcm.IsEmpty) return true;
            if (pcm.Length > capacityBytes - bufferedBytes) return false;
            chunks.Enqueue(pcm.ToArray());
            bufferedBytes += pcm.Length;
            return true;
        }
    }

    public void Complete(long id) { lock (gate) { if (id == generation) completed = true; } }
    public int BufferedBytes { get { lock (gate) return bufferedBytes; } }

    public int Read(Span<byte> buffer)
    {
        lock (gate)
        {
            var count = buffer.Length - buffer.Length % WaveFormat.BlockAlign;
            var written = 0;
            while (written < count && chunks.TryPeek(out var chunk))
            {
                var take = Math.Min(count - written, chunk.Length - offset);
                chunk.AsSpan(offset, take).CopyTo(buffer[written..]);
                written += take;
                offset += take;
                bufferedBytes -= take;
                if (offset == chunk.Length) { chunks.Dequeue(); offset = 0; }
            }
            if (written > 0 || completed) return written;
            // While an active stream waits for data, render silence without accumulating it.
            buffer[..count].Clear();
            return count;
        }
    }
}
