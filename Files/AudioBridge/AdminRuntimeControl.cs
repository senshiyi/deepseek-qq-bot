namespace AudioBridge;

/// <summary>In-process runtime controls; no commands, paths or credentials are accepted from a model.</summary>
public sealed class AdminRuntimeControl
{
    private readonly object gate = new();
    private Func<object>? snapshot;
    private Func<bool, Task>? pause;
    public void Bind(Func<object> getSnapshot, Func<bool, Task> setPaused)
    { lock (gate) { snapshot = getSnapshot; pause = setPaused; } }
    public void Clear() { lock (gate) { snapshot = null; pause = null; } }
    public object Snapshot()
    {
        Func<object>? current;
        lock (gate) current = snapshot;
        return current?.Invoke() ?? new { running = false, paused = true };
    }
    public async Task SetPausedAsync(bool value)
    {
        Func<bool, Task>? current;
        lock (gate) current = pause;
        if (current is null) throw new InvalidOperationException("消息服务尚未就绪，请稍后重试。");
        await current(value).ConfigureAwait(false);
    }
}
