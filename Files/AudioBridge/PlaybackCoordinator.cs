namespace AudioBridge;

// One output session at a time. Replacement waits for the old device owner to exit.
public sealed class PlaybackCoordinator : IAsyncDisposable
{
    private readonly CancellationToken lifetime;
    private readonly SemaphoreSlim transition = new(1, 1);
    private CancellationTokenSource? currentCancellation;
    private Task currentTask = Task.CompletedTask;
    private long generation;
    private bool muted;
    private bool disposed;
    private Exception? lastError;

    public PlaybackCoordinator(CancellationToken lifetime = default) { this.lifetime = lifetime; }

    public long Generation => Interlocked.Read(ref generation);
    public bool Muted => Volatile.Read(ref muted);
    public bool Playing => !currentTask.IsCompleted;
    public Exception? LastError => Volatile.Read(ref lastError);

    public async Task<long> StartAsync(Func<long, CancellationToken, Task> play)
    {
        await transition.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lifetime.ThrowIfCancellationRequested();
            await StopCoreAsync();
            var id = Interlocked.Increment(ref generation);
            if (muted) return id;
            lastError = null;
            currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            currentTask = RunAsync(id, play, currentCancellation.Token);
            return id;
        }
        finally { transition.Release(); }
    }

    private async Task RunAsync(long id, Func<long, CancellationToken, Task> play, CancellationToken ct)
    {
        try { await play(id, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { Volatile.Write(ref lastError, error); }
    }

    public async Task StopAsync()
    {
        await transition.WaitAsync();
        try { await StopCoreAsync(); }
        finally { transition.Release(); }
    }

    private async Task StopCoreAsync()
    {
        // Invalidate even if a service ignores cancellation and returns late.
        Interlocked.Increment(ref generation);
        if (currentCancellation is not null)
        {
            await currentCancellation.CancelAsync();
            await currentTask;
            currentCancellation.Dispose();
            currentCancellation = null;
        }
    }

    public async Task SetMutedAsync(bool value)
    {
        await transition.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            muted = value;
            if (value) await StopCoreAsync();
        }
        finally { transition.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await transition.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            await StopCoreAsync();
        }
        finally { transition.Release(); }
    }
}
