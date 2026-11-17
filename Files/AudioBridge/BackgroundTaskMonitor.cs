namespace AudioBridge;

public sealed record BackgroundTaskSnapshot(string Name, string State, DateTimeOffset? LastSuccessAt, string? LastError,
    string? LastResult = null, DateTimeOffset? LastResultAt = null, string? LastErrorCode = null, string? LastErrorMessage = null);

/// <summary>Owns the observable lifecycle of one background loop without retrying side effects.</summary>
internal sealed class BackgroundTaskMonitor(string name, Action<string, object> log, TimeProvider? clock = null)
{
    private readonly object gate = new();
    private readonly TimeProvider timeProvider = clock ?? TimeProvider.System;
    private string state = "starting";
    private DateTimeOffset? lastSuccessAt;
    private string? lastError;
    private string? lastResult;
    private DateTimeOffset? lastResultAt;
    private string? lastErrorCode;
    private string? lastErrorMessage;

    public BackgroundTaskSnapshot Snapshot()
    {
        lock (gate) return new(name, state, lastSuccessAt, lastError, lastResult, lastResultAt, lastErrorCode, lastErrorMessage);
    }

    /// <summary>事件驱动的后台（如语音输入）没有自己的循环：创建后直接标成运行中，第一次处理再记结果。</summary>
    public void MarkRunning() { lock (gate) if (state == "starting") state = "running"; }

    /// <summary>只记录这一次处理的结果；循环是否还在跑由 RunAsync 决定，单次失败不能显示成"已终止"。</summary>
    public void Report(Exception? error)
    {
        lock (gate)
        {
            // 循环已经退出后不再接受回调：迟到的成功不能把"已终止"洗掉。
            if (state is "failed" or "stopped") return;
            RecordResult(error);
            if (state == "starting") state = "running";
        }
    }

    private void RecordResult(Exception? error)
    {
        var now = timeProvider.GetUtcNow();
        var failure = error is null ? null : ReplyFailure.From(error);
        lastResult = error is null ? "succeeded" : "failed";
        lastResultAt = now;
        lastError = error?.GetType().Name;
        lastErrorCode = failure?.Code;
        lastErrorMessage = failure?.Message;
        if (error is null) lastSuccessAt = now;
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (gate) state = "stopped";
            return;
        }
        catch (Exception error)
        {
            // 循环真的退出才标 failed；面板据此区分"这一次失败"和"已经停了"。
            Report(ReplyFailure.Tag(error, "background_loop_failed"));
            lock (gate) state = "failed";
            var failure = ReplyFailure.From(error);
            log("background_task_failed", new { name, type = error.GetType().Name, code = failure.Code, reason = failure.Message });
            if (propagateFailure) throw;
            return;
        }
    }
}
