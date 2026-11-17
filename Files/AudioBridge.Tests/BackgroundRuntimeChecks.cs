using System.Net;
using System.Text.Json;
using AudioBridge;

internal static class BackgroundRuntimeChecks
{
    public static async Task RunAsync()
    {
        var clock = new FixedClock();
        var logged = new List<string>();
        var monitor = new BackgroundTaskMonitor("companion", (_, details) => logged.Add(JsonSerializer.Serialize(details)), clock);
        Check(monitor.Snapshot() is { State: "starting", LastSuccessAt: null, LastError: null }, "new task invented a successful tick");
        monitor.Report(null);
        var succeeded = monitor.Snapshot();
        clock.Now = clock.Now.AddMinutes(1);
        monitor.Report(new IOException("private response and signed URL must stay hidden"));
        Check(monitor.Snapshot() is { State: "running", LastError: nameof(IOException), LastResult: "failed" } failure &&
            failure.LastSuccessAt == succeeded.LastSuccessAt &&
            failure.LastErrorMessage!.Contains("数据读取", StringComparison.Ordinal),
            "a single failed tick looked like a terminated loop or exposed exception text");
        monitor.Report(null);
        Check(monitor.Snapshot() is { State: "running", LastError: null } recovered && recovered.LastSuccessAt == clock.Now,
            "successful later tick did not recover task health");

        await monitor.RunAsync(_ => Task.FromException(new FormatException("private output")), default);
        Check(monitor.Snapshot() is { State: "failed", LastError: nameof(FormatException), LastErrorCode: "background_loop_failed" } exited &&
            exited.LastErrorMessage!.Contains("后台循环异常退出", StringComparison.Ordinal) &&
            logged.Count == 1 && !logged[0].Contains("private output", StringComparison.Ordinal),
            "a terminated loop was not visible or leaked its body");
        monitor.Report(null);
        Check(monitor.Snapshot() is { State: "failed", LastErrorCode: "background_loop_failed" } late && late.LastResult == "failed",
            "a late callback washed away the terminated loop state");
        var earlyExit = new BackgroundTaskMonitor("health", (_, _) => { });
        await earlyExit.RunAsync(_ => Task.CompletedTask, default);
        Check(earlyExit.Snapshot() is { State: "failed", LastErrorCode: "background_loop_failed" }, "unexpected clean exit looked healthy");

        var passive = new BackgroundTaskMonitor("voice-input", (_, _) => { });
        passive.MarkRunning();
        Check(passive.Snapshot() is { State: "running", LastResult: null, LastSuccessAt: null },
            "an event driven task looked like it never started or invented a success");
        var cancelled = new BackgroundTaskMonitor("cancelled", (_, _) => throw new Exception("normal cancellation logged as a failure"));
        using (var stop = new CancellationTokenSource())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = cancelled.RunAsync(async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); }, stop.Token);
            await entered.Task;
            stop.Cancel();
            await run;
        }
        Check(cancelled.Snapshot() is { State: "stopped", LastSuccessAt: null, LastError: null }, "cancellation invented success or failure");
        var required = new BackgroundTaskMonitor("required", (_, _) => { });
        try
        {
            await required.RunAsync(_ => Task.FromException(new IOException("required task failed")), default, propagateFailure: true);
            throw new Exception("required task failure no longer reaches the host");
        }
        catch (IOException) { }
        Check(required.Snapshot().State == "failed", "required task failure was not observable");

        try { GroupChatBrain.Parse("invalid private JSON", new HashSet<string>()); throw new Exception("invalid model JSON accepted"); }
        catch (InvalidDataException error) { Check(!error.Message.Contains("private", StringComparison.Ordinal), "model JSON leaked into parse error"); }

        var directory = Path.Combine(Path.GetTempPath(), "qq-background-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await RejectAsync("network", (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("private request URL")));
            await RejectAsync("bad-json", (_, _) => Task.FromResult(Response("data: {not-json}\n\n")));
            await RejectAsync("bad-shape", (_, _) => Task.FromResult(Response("data: {\"choices\":{}}\n\n")));
            await RejectAsync("timeout", async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Response("data: [DONE]\n\n"); },
                TimeSpan.FromMilliseconds(40));
            await RejectStatus(HttpStatusCode.Unauthorized, "deepseek_auth");
            await RejectStatus(HttpStatusCode.PaymentRequired, "deepseek_balance");
            await RejectStatus(HttpStatusCode.BadRequest, "deepseek_rejected");
            await RejectStatus(HttpStatusCode.ServiceUnavailable, "deepseek_unavailable");

            using var stop = new CancellationTokenSource();
            using var provider = new DeepSeekReply("test-key", Budget("cancel"), new Handler((_, ct) =>
            {
                stop.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(ct);
            }));
            try
            {
                await foreach (var _ in provider.StreamAsync([new("user", "test")], stop.Token)) { }
                throw new Exception("caller cancellation was swallowed");
            }
            catch (OperationCanceledException error) { Check(error.CancellationToken == stop.Token, "caller cancellation lost its token"); }

            ApiBudget Budget(string name) => new(Path.Combine(directory, name + ".json"), .3, 10);
            async Task RejectStatus(HttpStatusCode status, string code)
            {
                using var reply = new DeepSeekReply("test-key", Budget("status-" + (int)status),
                    new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
                try
                {
                    await foreach (var _ in reply.StreamAsync([new("user", "test")], default)) { }
                    throw new Exception("HTTP " + (int)status + " was accepted");
                }
                catch (IOException error)
                {
                    var failure = ReplyFailure.From(error);
                    Check(failure.Code == code && !error.Message.Contains("test-key", StringComparison.Ordinal),
                        "HTTP " + (int)status + " mapped to " + failure.Code + " or leaked the key");
                }
            }
            async Task RejectAsync(string name, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeSpan? timeout = null)
            {
                using var reply = timeout is { } limit
                    ? new DeepSeekReply("test-key", Budget(name), new Handler(send), limit)
                    : new DeepSeekReply("test-key", Budget(name), new Handler(send));
                try
                {
                    await foreach (var _ in reply.StreamAsync([new("user", "test")], default)) { }
                    throw new Exception(name + " failure was accepted");
                }
                catch (IOException error)
                {
                    Check(!error.Message.Contains("private", StringComparison.Ordinal) && error.InnerException is null,
                        name + " error exposed private transport details");
                    Check(ReplyFailure.From(error).Code == "deepseek_unavailable", name + " failure was not classified for the panel");
                    Check(File.Exists(Path.Combine(directory, name + ".json")), name + " failure refunded its uncertain request");
                }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static HttpResponseMessage Response(string events) => new(HttpStatusCode.OK) { Content = new StringContent(events) };
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
