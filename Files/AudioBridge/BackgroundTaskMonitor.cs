namespace AudioBridge;

public sealed record BackgroundTaskSnapshot(string Name, string State, DateTimeOffset? LastSuccessAt, string? LastError);

/// <summary>Owns the observable lifecycle of one background loop without retrying side effects.</summary>
internal sealed class BackgroundTaskMonitor(string name, Action<string, object> log, TimeProvider? clock = null)
{
    private readonly object gate = new();
    private readonly TimeProvider timeProvider = clock ?? TimeProvider.System;
    private string state = "starting";
    private DateTimeOffset? lastSuccessAt;
    private string? lastError;

    public BackgroundTaskSnapshot Snapshot()
    {
        lock (gate) return new(name, state, lastSuccessAt, lastError);
    }

    public void Report(Exception? error)
    {
        lock (gate)
        {
            state = error is null ? "running" : "failed";
            lastError = error?.GetType().Name;
            if (error is null) lastSuccessAt = timeProvider.GetUtcNow();
        }
    }

    public async Task RunAsync(Func<CancellationToken, Task> body, CancellationToken ct, bool propagateFailure = false)
    {
        lock (gate) state = "running";
        try
        {
            await body(ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
                throw new InvalidOperationException("后台循环意外结束。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            Report(error);
            log("background_task_failed", new { name, type = error.GetType().Name });
            if (propagateFailure) throw;
            return;
        }
        lock (gate) state = "stopped";
    }
}
