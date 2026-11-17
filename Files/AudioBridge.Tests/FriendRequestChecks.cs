using System.Collections.Concurrent;
using System.Text.Json;
using AudioBridge;

public static class FriendRequestChecks
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("one exact approval per friend request with a bounded flag", Approval),
            ("paused, preview and malformed requests never approve", Refusals),
            ("hourly and daily limits cap automatic approvals", Limits),
            ("a second request waits until the first approval finishes", SingleFlight),
            ("failed approvals are reported and release the gate", Failure)
        };
        foreach (var check in checks)
        {
            await check.Run().WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine("PASS friend requests: " + check.Name);
        }
        return checks.Length;
    }

    private static async Task Approval()
    {
        using var harness = new Harness();
        var acceptor = harness.Acceptor();
        Check(acceptor.TryHandle(Event(flag: "flag-1")), "friend request was not recognised");
        var (status, details) = await harness.NextAsync();
        Check(status == "friend_request" && details.Contains("\"approved\":true", StringComparison.Ordinal) &&
              details.Contains("\"userId\":20", StringComparison.Ordinal), "approval was not reported");
        Check(harness.Approved.SequenceEqual(["flag-1"]), "approval did not use the event flag exactly once");
    }

    private static async Task Refusals()
    {
        using var harness = new Harness();
        Check(!harness.Acceptor().TryHandle(Event(postType: "message")), "message event was claimed as a friend request");
        Check(!harness.Acceptor().TryHandle(Event(requestType: "group")), "group request was claimed as a friend request");
        harness.Paused = true;
        Check(harness.Acceptor().TryHandle(Event(flag: "paused")), "paused acceptor did not take the event");
        Check((await harness.NextAsync()).status == "friend_request_ignored", "paused request was not reported");
        Check(harness.Approved.Count == 0, "paused acceptor still approved");
        harness.Paused = false;
        Check(harness.Acceptor(live: false).TryHandle(Event(flag: "preview")), "preview acceptor did not take the event");
        Check(harness.Approved.Count == 0, "preview run approved a real friend request");
        Check(harness.Acceptor(selfId: 99).TryHandle(Event(selfId: 98, flag: "wrong-account")), "wrong account request was not claimed");
        Check(harness.Acceptor().TryHandle(Event(flag: null)), "request without a flag was not claimed");
        Check(harness.Acceptor().TryHandle(Event(flag: "a\0b")), "control characters in the flag were not claimed");
        Check(harness.Acceptor().TryHandle(Event(flag: 7)), "non-string flag was not claimed");
        var limits = 0;
        while (limits++ < 3) await harness.NextAsync();
        Check(harness.Approved.Count == 0, "malformed or refused requests still approved");
        try { harness.Acceptor(selfId: 0); throw new Exception("missing account accepted"); }
        catch (ArgumentOutOfRangeException) { }
    }

    private static async Task Limits()
    {
        using var hourly = new Harness();
        var acceptor = hourly.Acceptor();
        for (var index = 0; index < 4; index++)
        {
            Check(acceptor.TryHandle(Event(flag: "hour-" + index)), "hourly request was not claimed");
            var entry = await hourly.NextAsync();
            if (index < 3) Check(entry.status == "friend_request", "request inside the hourly limit was refused");
            else Check(entry.status == "friend_request_ignored" && entry.details.Contains("rate_limited", StringComparison.Ordinal),
                "fourth request within one hour was approved");
        }
        Check(hourly.Approved.Count == 3, "hourly limit did not stop the fourth approval");
        hourly.Clock = hourly.Clock.AddHours(2);
        Check(acceptor.TryHandle(Event(flag: "hour-next")), "request after the hourly window was not claimed");
        Check((await hourly.NextAsync()).status == "friend_request", "rolling hour window did not reopen");
        using var daily = new Harness();
        var cap = daily.Acceptor();
        for (var index = 0; index < 22; index++)
        {
            cap.TryHandle(Event(flag: "day-" + index));
            await daily.NextAsync();
            daily.Clock = daily.Clock.AddMinutes(61);
        }
        Check(daily.Approved.Count == FriendRequestAcceptor.PerDayLimit, "daily limit did not cap approvals at 20");
    }

    private static async Task SingleFlight()
    {
        using var harness = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ApproveOverride = (_, _) => release.Task;
        var acceptor = harness.Acceptor();
        Check(acceptor.TryHandle(Event(flag: "first")), "first request was not claimed");
        while (harness.Approved.Count == 0) await Task.Delay(10);
        Check(acceptor.TryHandle(Event(flag: "second")), "second request was not claimed");
        var skipped = await harness.NextAsync();
        Check(skipped.status == "friend_request_ignored" && skipped.details.Contains("busy", StringComparison.Ordinal),
            "overlapping friend request was not skipped");
        release.SetResult();
        Check((await harness.NextAsync()).status == "friend_request", "first approval never finished");
        Check(harness.Approved.SequenceEqual(["first"]), "overlapping request was approved concurrently");
        Check(acceptor.TryHandle(Event(flag: "third")), "request after completion was not claimed");
        Check((await harness.NextAsync()).status == "friend_request" && harness.Approved.SequenceEqual(["first", "third"]),
            "gate did not reopen after a completed approval");
    }

    private static async Task Failure()
    {
        using var harness = new Harness();
        harness.ApproveOverride = (_, _) => throw new OneBotMessageException("synthetic failure", false);
        var acceptor = harness.Acceptor();
        Check(acceptor.TryHandle(Event(flag: "broken")), "failing request was not claimed");
        var failure = await harness.NextAsync();
        Check(failure.status == "friend_request_failed" && failure.details.Contains("OneBotMessageException", StringComparison.Ordinal),
            "approval failure was not reported");
        harness.ApproveOverride = null;
        Check(acceptor.TryHandle(Event(flag: "working")), "request after a failure was not claimed");
        Check((await harness.NextAsync()).status == "friend_request" && harness.Approved.SequenceEqual(["broken", "working"]),
            "failed approval kept the gate closed");
    }

    private static JsonElement Event(string postType = "request", string requestType = "friend", long userId = 20,
        object? flag = null, long selfId = 99)
    {
        var body = new Dictionary<string, object?>
        {
            ["post_type"] = postType, ["request_type"] = requestType, ["user_id"] = userId, ["self_id"] = selfId
        };
        if (flag is not null) body["flag"] = flag;
        return JsonSerializer.SerializeToElement(body);
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }

    private sealed class Harness : IDisposable
    {
        private readonly ConcurrentQueue<(string status, string details)> entries = new();
        private readonly SemaphoreSlim signal = new(0);
        public List<string> Approved { get; } = [];
        public Func<string, CancellationToken, Task>? ApproveOverride { get; set; }
        public DateTimeOffset Clock { get; set; } = Now;
        public bool Paused { get; set; }

        public FriendRequestAcceptor Acceptor(long selfId = 99, bool live = true) => new(
            (flag, ct) => { lock (Approved) Approved.Add(flag); return ApproveOverride?.Invoke(flag, ct) ?? Task.CompletedTask; },
            () => Paused, selfId,
            (status, details) => { entries.Enqueue((status, JsonSerializer.SerializeToElement(details).GetRawText())); signal.Release(); },
            live, () => Clock);

        public async Task<(string status, string details)> NextAsync()
        {
            if (!await signal.WaitAsync(TimeSpan.FromSeconds(10))) throw new Exception("expected log entry never arrived");
            entries.TryDequeue(out var entry);
            return entry;
        }

        public void Dispose() => signal.Dispose();
    }
}
