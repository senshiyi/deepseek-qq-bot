using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioBridge;

public sealed record AudioDeviceInfo(string Id, string Name, string Direction);

public static class NativeAudio
{
    public static IReadOnlyList<AudioDeviceInfo> ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
        var result = new List<AudioDeviceInfo>();
        foreach (var device in devices)
        {
            using (device)
            {
                result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName,
                    device.DataFlow == DataFlow.Render ? "render" : "capture"));
            }
        }
        return result;
    }

    /// <summary>
    /// Captures only the selected process tree. Callback memory is valid only until the callback returns.
    /// </summary>
    public static async Task CaptureProcessAsync(int processId,
        Action<ReadOnlyMemory<byte>, WaveFormat> onData, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentNullException.ThrowIfNull(onData);
        cancellationToken.ThrowIfCancellationRequested();

        using var process = Process.GetProcessById(processId);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnProcessExited(object? sender, EventArgs args) => completion.TrySetException(
            new InvalidOperationException($"目标进程 {processId} 已退出，按进程采集已停止。"));
        using var cancellationRegistration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));

        static void DisposeAfterActivation(Task<WasapiRecorder> activation)
        {
            // An abandoned activation must never start recording, even if it completes later.
            var cleanup = Task.Run(async () =>
            {
                var lateRecorder = await activation.ConfigureAwait(false);
                await lateRecorder.DisposeAsync().ConfigureAwait(false);
            });
            _ = cleanup.ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        WasapiRecorder recorder;
        process.Exited += OnProcessExited;
        try
        {
            process.EnableRaisingEvents = true;
            if (process.HasExited)
                throw new InvalidOperationException($"目标进程 {processId} 已退出。");
            cancellationToken.ThrowIfCancellationRequested();

            // BuildAsync itself may block before returning its Task, so invoke it on a worker too.
            var activation = Task.Run(async () => await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)processId, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithBufferLength(20)
                .BuildAsync().ConfigureAwait(false));
            Task ready;
            try
            {
                ready = await Task.WhenAny(activation, completion.Task)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                // Preserve an activation's own failure instead of relabelling it as a wait timeout.
                if (activation.IsFaulted || activation.IsCanceled)
                    await activation.ConfigureAwait(false);
                DisposeAfterActivation(activation);
                throw new TimeoutException(
                    "按进程采集激活的等待已取消或超时；后台将于激活返回后释放，资源尚未确认释放。", exception);
            }
            if (ready != activation && !activation.IsCompleted)
            {
                DisposeAfterActivation(activation);
                var reason = completion.Task.Exception?.InnerException
                    ?? new OperationCanceledException(cancellationToken);
                throw new TimeoutException(
                    "采集激活尚未完成，目标进程或等待已结束；后台将于激活返回后释放，资源尚未确认释放。", reason);
            }
            recorder = await activation.ConfigureAwait(false);
        }
        catch
        {
            process.Exited -= OnProcessExited;
            throw;
        }

        byte[] callbackBuffer = [];
        void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags,
            long devicePosition, long qpcPosition)
        {
            if (completion.Task.IsCompleted)
                return;
            try
            {
                if (callbackBuffer.Length < data.Length)
                {
                    Array.Clear(callbackBuffer);
                    callbackBuffer = new byte[data.Length];
                }
                if ((flags & AudioClientBufferFlags.Silent) != 0)
                    callbackBuffer.AsSpan(0, data.Length).Clear();
                else
                    data.CopyTo(callbackBuffer);
                if (!completion.Task.IsCompleted && !cancellationToken.IsCancellationRequested)
                    onData(callbackBuffer.AsMemory(0, data.Length), recorder.WaveFormat);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                recorder.StopRecording();
            }
        }
        void OnStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception is not null)
                completion.TrySetException(args.Exception);
            else if (cancellationToken.IsCancellationRequested)
                completion.TrySetCanceled(cancellationToken);
            else
                completion.TrySetException(new IOException("按进程音频采集意外停止。"));
        }

        recorder.DataAvailable += OnData;
        recorder.RecordingStopped += OnStopped;
        var recordingStarted = false;
        var startupAbandoned = false;
        Task? startup = null;
        async Task DisposeRecorderAsync()
        {
            if (startup is not null)
            {
                try
                {
                    await startup.ConfigureAwait(false);
                }
                catch
                {
                    // A failed synchronous initialization can leave Starting without a worker.
                    // Its error was already propagated (or the abandoned task is now observed).
                }
            }
            // Do not let StopRecording during Starting be overwritten by the NAudio worker.
            while (recordingStarted && recorder.CaptureState == CaptureState.Starting)
                await Task.Delay(1).ConfigureAwait(false);
            try
            {
                await recorder.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                recorder.DataAvailable -= OnData;
                recorder.RecordingStopped -= OnStopped;
                Array.Clear(callbackBuffer);
            }
        }
        try
        {
            if (completion.Task.IsCompleted)
                await completion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"目标进程 {processId} 已退出。");

            startup = Task.Run(() =>
            {
                // A queued start must not run after cancellation/process exit abandoned it.
                if (completion.Task.IsCompleted)
                    return;
                cancellationToken.ThrowIfCancellationRequested();
                recorder.StartRecording();
                recordingStarted = true;
            });
            try
            {
                await startup.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                if (startup.IsFaulted || startup.IsCanceled)
                    await startup.ConfigureAwait(false);
                startupAbandoned = true;
                throw new TimeoutException(
                    "按进程采集启动的等待已取消或超时；后台将于启动返回后清理，资源尚未确认释放。", exception);
            }
            // NAudio 3.2.0 sets Capturing on its worker after StartRecording returns. Stopping
            // during Starting can be overwritten by that worker; wait for the handoff first.
            var startupWait = Stopwatch.StartNew();
            while (recorder.CaptureState == CaptureState.Starting)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (startupWait.Elapsed > TimeSpan.FromSeconds(5))
                    throw new TimeoutException("底层按进程采集线程在 5 秒内未完成启动。");
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            process.Exited -= OnProcessExited;
            completion.TrySetCanceled();
            // Cleanup continues if a native call is stuck; report this instead of claiming release.
            // DisposeAsync can also block before returning its Task, so invoke it on a worker.
            var cleanup = Task.Run(DisposeRecorderAsync);
            if (startupAbandoned)
            {
                _ = cleanup.ContinueWith(task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else
            {
                try
                {
                    await cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (TimeoutException exception)
                {
                    _ = cleanup.ContinueWith(task => _ = task.Exception,
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw new TimeoutException("底层采集线程在 5 秒内未退出；后台仍在清理，资源尚未确认释放。", exception);
                }
            }
        }
    }

    /// <summary>Plays on the specified active render endpoint without changing the default device.</summary>
    public static async Task PlayAsync(string renderDeviceId, IWaveProvider source,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(renderDeviceId);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(renderDeviceId);
        if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
            throw new InvalidOperationException("指定的播放设备不是活动的 render 端点。");

        var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithLatency(40)
            .Build();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception is not null)
                completion.TrySetException(args.Exception);
            else
                completion.TrySetResult();
        }

        player.PlaybackStopped += OnStopped;
        try
        {
            player.Init(source);
            cancellationToken.ThrowIfCancellationRequested();
            player.Play();
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            await player.DisposeAsync().ConfigureAwait(false);
            player.PlaybackStopped -= OnStopped;
        }
    }
}
