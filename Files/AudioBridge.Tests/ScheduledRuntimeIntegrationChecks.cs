using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AudioBridge;

public static class ScheduledRuntimeIntegrationChecks
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 1, 0, 0, TimeSpan.Zero);
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("durable once delivery records confirmed context only and does not requeue", Confirmed),
            ("preview never touches OneBot, context or independent allowance", Preview),
            ("edit during identity lookup cancels the old revision", EditCancels),
            ("pause fences queued claims even after immediate resume", PauseQueued),
            ("resume consumes due occurrences missed while paused without an intervening poll", PauseWithoutPoll),
            ("offline at due time is consumed even when cached account data remains available", OfflineDue),
            ("a disconnect after identity lookup prevents submission and reconnect does not replay it", OfflineBeforeSubmit),
            ("observed disconnect invalidates already queued work across reconnect", OfflineQueued),
            ("a ten-second scheduler suspension skips due and already queued work inside the delivery window", OfflineGap),
            ("suspension cancels identity lookup and preserves uncertainty after HTTP submission", OfflineGapInFlight),
            ("an uncertain HTTP send is durably marked and never retried", UnknownDelivery),
            ("pause during a submitted send retains uncertainty without context", PauseSending),
            ("account replacement pauses before sending any task", AccountReplacement),
            ("target revocation during login prevents the final send", Revocation),
            ("unknown memes fail before identity or message HTTP", UnknownMeme),
            ("independent rate limits survive session replacement and separate targets", IndependentLimits),
            ("authenticated HTTP CRUD validates targets, memes and revisions without chat reload", AdminCrud),
            ("ordinary and scheduled messages share the same serial worker and release resources", SharedWorker)
        };
        foreach (var check in checks)
        { await check.Run().WaitAsync(TimeSpan.FromSeconds(15)); Console.WriteLine("PASS schedule integration: " + check.Name); }
        return checks.Length;
    }

    private static async Task Confirmed()
    {
        using var fixture = new Fixture();
        await using var runtime = fixture.Runtime();
        var claim = fixture.Due(runtime);
        var result = await runtime.HandleAsync(claim, default);
        Check(result.Status == "api_accepted" && fixture.Api.Sends == 1 && fixture.Confirmed == 1, "confirmed send not recorded once");
        Check(fixture.Store.List().Single().LastRun is { Status: "api_accepted", MessageId: "901" }, "receipt was not durable");
        Check(runtime.Poll(Start).Count == 0, "confirmed occurrence requeued");
        Check((await runtime.HandleAsync(claim, default)).Status != "api_accepted" && fixture.Api.Sends == 1, "old claim resent");
    }
    private static async Task Preview()
    {
        using var fixture = new Fixture();
        await using var runtime = fixture.Runtime(false);
        var outcome = await runtime.HandleAsync(fixture.Due(runtime), default);
        Check(outcome.Status == "preview" && fixture.Api.Calls == 0 && fixture.Confirmed == 0 && fixture.Limits.Snapshot().Length == 0,
            "preview consumed transport, history or rate allowance");
    }
    private static async Task EditCancels()
    {
        using var fixture = new Fixture(); fixture.Api.BlockLogin = true;
        await using var runtime = fixture.Runtime();
        var claim = fixture.Due(runtime); var handling = runtime.HandleAsync(claim, default);
        await fixture.Api.LoginStarted.Task;
        fixture.Store.Save(claim.Definition with { Text = "新的固定内容", OnceAt = fixture.Clock.Now.AddMinutes(5) }, [10], [20], claim.TaskId, claim.Revision);
        runtime.Invalidate(claim.TaskId);
        var outcome = await handling;
        fixture.Api.Release.TrySetResult();
        Check(outcome.Status == "cancelled" && fixture.Api.Sends == 0 && fixture.Confirmed == 0 && fixture.Store.List().Single().LastRun is null,
            "edited claim sent or overwrote the new revision");
    }
    private static async Task PauseQueued()
    {
        using var fixture = new Fixture();
        await using var runtime = fixture.Runtime();
        var claim = fixture.Due(runtime);
        await runtime.SetPausedAsync(true); await runtime.SetPausedAsync(false);
        var result = await runtime.HandleAsync(claim, default);
        Check(result.Status != "api_accepted" && fixture.Api.Calls == 0 && fixture.Store.List().Single().LastRun?.Status == "skipped_paused",
            "pause-resume replayed queued old work");
    }
    private static async Task PauseWithoutPoll()
    {
        using var fixture = new Fixture();
        await using var runtime = fixture.Runtime();
        fixture.Store.Save(new() { TargetId = 10, Enabled = true, OnceAt = fixture.Clock.Now.AddSeconds(1), Text = "暂停期间到期" }, [10], [20]);
        Check(runtime.Poll(Start).Count == 0, "future task was already due");
        await runtime.SetPausedAsync(true);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(2);
        await runtime.SetPausedAsync(false);
        Check(runtime.Poll(Start).Count == 0 && fixture.Store.List().Single().LastRun?.Status == "skipped_paused" && fixture.Api.Sends == 0,
            "resuming before the next poll caught up a task that became due while paused");
        var result = await runtime.HandleAsync(fixture.Due(runtime), default);
        Check(result.Status == "api_accepted", "resuming suppressed a future newly due task");
    }
    private static async Task OfflineDue()
    {
        foreach (var variant in new[] { "offline", "bad", "unavailable" })
        {
            using var fixture = new Fixture();
            fixture.Api.Online = variant != "offline"; fixture.Api.Good = variant != "bad"; fixture.Api.FailStatus = variant == "unavailable";
            await using var runtime = fixture.Runtime();
            fixture.CreateDue();
            Check((await runtime.PollAsync(Start, default)).Count == 0 && fixture.Store.List().Single().LastRun?.Status == "skipped_offline" && fixture.Api.Sends == 0,
                "due occurrence survived observed " + variant + " connection");
            fixture.Api.Online = true; fixture.Api.Good = true; fixture.Api.FailStatus = false;
            Check((await runtime.PollAsync(Start, default)).Count == 0 && fixture.Api.Sends == 0 && fixture.Api.Logins == 0,
                "reconnect caught up skipped occurrence or depended on cached account data");
        }
    }
    private static async Task OfflineBeforeSubmit()
    {
        using var fixture = new Fixture();
        await using var runtime = fixture.Runtime();
        fixture.CreateDue();
        var claim = (await runtime.PollAsync(Start, default)).Single();
        fixture.Api.OfflineAfterLogin = true;
        Check((await runtime.HandleAsync(claim, default)).Status == "skipped_offline" && fixture.Api.Sends == 0 && fixture.Confirmed == 0,
            "cached identity allowed a disconnected send");
        fixture.Api.Online = true; fixture.Api.OfflineAfterLogin = false;
        Check(runtime.Poll(Start).Count == 0 && (await runtime.HandleAsync(claim, default)).Status != "api_accepted" && fixture.Api.Sends == 0,
            "reconnect replayed work cancelled before submission");
    }
    private static async Task OfflineQueued()
    {
        using var fixture = new Fixture();
        await using var runtime = fixture.Runtime();
        var claim = fixture.Due(runtime);
        runtime.NotifyOffline();
        Check((await runtime.HandleAsync(claim, default)).Status == "skipped_offline" && fixture.Api.Calls == 0 &&
            fixture.Store.List().Single().LastRun?.Status == "skipped_offline", "reconnected queued occurrence survived an observed disconnect");
    }
    private static async Task UnknownDelivery()
    {
        using var fixture = new Fixture(); fixture.Api.FailSend = true;
        await using var runtime = fixture.Runtime();
        var claim = fixture.Due(runtime); var result = await runtime.HandleAsync(claim, default);
        Check(result.Status == "delivery_unknown" && fixture.Api.Sends == 1 && fixture.Confirmed == 0, "ambiguous send was treated as a known failure or history");
        using var reopened = new Fixture(fixture.Root, fixture.Clock);
        Check(reopened.Store.List().Single().LastRun?.Status == "delivery_unknown" && reopened.Store.ClaimDue([10], [20], false, Start).Count == 0,
            "restart retried uncertain delivery");
    }
    private static async Task OfflineGap()
    {
        using var fixture = new Fixture();
        await using var runtime = fixture.Runtime();
        _ = runtime.Poll(Start);
        fixture.Store.Save(new() { TargetId = 10, Enabled = true, OnceAt = fixture.Clock.Now.AddSeconds(2), Text = "休眠时刻" }, [10], [20]);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(10);
        Check(runtime.Poll(Start).Count == 0 && fixture.Store.List().Single().LastRun?.Status == "skipped_offline" && fixture.Api.Calls == 0,
            "short sleep caught up a due task inside the 60-second window");
        var queued = fixture.Due(runtime);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(10);
        Check((await runtime.HandleAsync(queued, default)).Status == "skipped_offline" && fixture.Api.Calls == 0,
            "queued task sent before the main poll could observe resume");
        Check((await runtime.HandleAsync(fixture.Due(runtime), default)).Status == "api_accepted" && fixture.Api.Sends == 1,
            "offline cutoff suppressed a future newly due task");
    }
    private static async Task OfflineGapInFlight()
    {
        foreach (var submitted in new[] { false, true })
        {
            using var fixture = new Fixture(); fixture.Api.BlockLogin = !submitted; fixture.Api.BlockSend = submitted;
            await using var runtime = fixture.Runtime();
            var handling = runtime.HandleAsync(fixture.Due(runtime), default);
            await (submitted ? fixture.Api.SendStarted.Task : fixture.Api.LoginStarted.Task);
            fixture.Clock.Now = fixture.Clock.Now.AddSeconds(10); _ = runtime.Poll(Start);
            var outcome = await handling;
            var expected = submitted ? "delivery_unknown" : "skipped_offline";
            Check(outcome.Status == expected && fixture.Store.List().Single().LastRun?.Status == expected && fixture.Confirmed == 0 &&
                fixture.Api.Sends == (submitted ? 1 : 0), "resume misclassified an unsubmitted/submitted task or archived it");
        }
    }
    private static async Task PauseSending()
    {
        using var fixture = new Fixture(); fixture.Api.BlockSend = true;
        await using var runtime = fixture.Runtime();
        var handling = runtime.HandleAsync(fixture.Due(runtime), default); await fixture.Api.SendStarted.Task;
        await runtime.SetPausedAsync(true); var result = await handling;
        Check(result.Status == "delivery_unknown" && fixture.Store.List().Single().LastRun?.Status == "delivery_unknown" && fixture.Confirmed == 0,
            "submitted cancellation promised no delivery or archived unsent content");
    }
    private static async Task AccountReplacement()
    {
        using var fixture = new Fixture(); fixture.Api.Account = 88;
        await using var runtime = fixture.Runtime();
        var result = await runtime.HandleAsync(fixture.Due(runtime), default);
        Check(result.Status == "account_changed_paused" && fixture.AccountPaused && fixture.Api.Sends == 0 && fixture.Confirmed == 0 &&
            fixture.Store.List().Single().LastRun?.Status == "account_changed_paused", "changed account sent or failed to pause");
    }
    private static async Task Revocation()
    {
        using var fixture = new Fixture(); fixture.Api.BlockLogin = true;
        await using var runtime = fixture.Runtime();
        var handling = runtime.HandleAsync(fixture.Due(runtime), default); await fixture.Api.LoginStarted.Task;
        fixture.Scope = new(99, [], [20], false); fixture.Api.Release.TrySetResult();
        Check((await handling).Status == "skipped_not_authorized" && fixture.Api.Sends == 0 && fixture.Confirmed == 0,
            "target revoked during identity HTTP was still used");
    }
    private static async Task UnknownMeme()
    {
        using var fixture = new Fixture();
        await using var runtime = fixture.Runtime();
        var result = await runtime.HandleAsync(fixture.Due(runtime, meme: "not_in_library"), default);
        Check(result.Status == "failed" && fixture.Api.Calls == 0 && fixture.Confirmed == 0, "unknown task meme reached HTTP");
    }
    private static async Task IndependentLimits()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 7; index++)
        {
            await using var runtime = fixture.Runtime(); // shared host limiter survives a session replacement.
            var outcome = await runtime.HandleAsync(fixture.Due(runtime), default);
            Check(outcome.Status == (index < 6 ? "api_accepted" : "skipped_busy"), "hourly independent limit or reload boundary failed");
            fixture.Clock.Now = fixture.Clock.Now.AddSeconds(61);
        }
        await using var other = fixture.Runtime();
        Check((await other.HandleAsync(fixture.Due(other, isPrivate: true), default)).Status == "api_accepted", "group quota leaked to private target");
        Check((await other.HandleAsync(fixture.Due(other, isPrivate: true), default)).Status == "skipped_busy", "60-second interval was ignored");
        Check(fixture.Api.Sends == 7 && fixture.Limits.Snapshot().Single(value => value.TargetType == "group").AttemptsLastHour == 6,
            "rate snapshot or actual send count wrong");
    }

    private static async Task AdminCrud()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "group-chat.local.json"), JsonSerializer.Serialize(new GroupChatSettings {
            SelfId = 99, AllowedGroupIds = [10, 11], ActiveGroupId = 10, AllowedPrivateUserIds = [20] }, BridgeConfig.JsonOptions));
        var changes = new List<string>(); var controls = 0; var port = Port();
        var server = new LocalAdminServer(fixture.Root, Token, () => new { running = true, groupId = 10 }, _ => { controls++; return Task.CompletedTask; },
            port, scheduledMessages: fixture.Store, scheduleChanged: changes.Add);
        using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(server.Prefix) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null) => await http.SendAsync(new HttpRequestMessage(method, path) {
            Content = body is null ? null : new StringContent(JsonSerializer.Serialize(body, BridgeConfig.JsonOptions), Encoding.UTF8, "application/json") });
        var draft = new ScheduledMessageDraft { TargetId = 10, OnceAt = fixture.Clock.Now.AddMinutes(5), Text = "固定测试内容" };
        try
        {
            using var created = await Send(HttpMethod.Post, "api/schedules", draft);
            Check(created.IsSuccessStatusCode, "authorized schedule HTTP save rejected");
            var task = JsonSerializer.Deserialize<ScheduledMessageTask>(await created.Content.ReadAsStringAsync(), BridgeConfig.JsonOptions)!;
            Check(changes.SequenceEqual([task.Id]) && !task.Definition.Enabled && controls == 0, "CRUD reloaded chat or activated by default");
            using var badTarget = await Send(HttpMethod.Post, "api/schedules", draft with { TargetId = 11 });
            using var badPrivate = await Send(HttpMethod.Post, "api/schedules", draft with { TargetType = "private", TargetId = 21 });
            using var badMeme = await Send(HttpMethod.Post, "api/schedules", draft with { MemeId = "unknown" });
            Check(badTarget.StatusCode == HttpStatusCode.BadRequest && badPrivate.StatusCode == HttpStatusCode.BadRequest && badMeme.StatusCode == HttpStatusCode.BadRequest,
                "HTTP accepted inactive group, unapproved contact or unknown meme");
            using var updated = await Send(HttpMethod.Put, "api/schedules/" + task.Id, new { revision = task.Revision, definition = draft with { ScheduleType = "daily", OnceAt = (DateTimeOffset?)null, DailyTime = "20:00", MemeId = "qq_face_14" } });
            Check(updated.IsSuccessStatusCode, "registered native face or daily edit rejected");
            using var stale = await Send(HttpMethod.Delete, "api/schedules/" + task.Id + "?revision=1");
            Check(stale.StatusCode == HttpStatusCode.BadRequest && fixture.Store.List().Count == 1, "stale delete removed current version");
            using var deleted = await Send(HttpMethod.Delete, "api/schedules/" + task.Id + "?revision=2");
            Check(deleted.IsSuccessStatusCode && fixture.Store.List().Count == 0 && changes.Count == 3 && controls == 0, "delete did not invalidate without reload");
            using var unauthenticated = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var denied = await unauthenticated.GetAsync(server.Prefix + "api/schedules");
            Check(denied.StatusCode == HttpStatusCode.Unauthorized, "schedule content leaked without bearer");
        }
        finally { stop.Cancel(); await running; }
    }

    private static async Task SharedWorker()
    {
        using var fixture = new Fixture(); var beforeDirectory = Environment.CurrentDirectory;
        var beforeToken = Environment.GetEnvironmentVariable("ONEBOT_ACCESS_TOKEN"); var beforeOutput = Console.Out;
        using var output = new StringWriter(); using var stop = new CancellationTokenSource(); using var fakeApi = new HttpListener();
        var port = Port(); var eventPort = Port(); while (eventPort == port) eventPort = Port();
        fakeApi.Prefixes.Add($"http://127.0.0.1:{port}/"); fakeApi.Start(); using var close = stop.Token.Register(fakeApi.Close);
        var firstSend = Ready(); var release = Ready(); var secondSend = Ready(); var sends = 0; var statusChecks = 0;
        var apiTask = Task.Run(async () =>
        {
            var handlers = new List<Task>();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await fakeApi.GetContextAsync();
                    handlers.Add(Task.Run(async () =>
                    {
                        var login = context.Request.Url!.AbsolutePath == "/get_login_info";
                        var status = context.Request.Url.AbsolutePath == "/get_status";
                        var offlineHealth = status && Interlocked.Increment(ref statusChecks) == 1;
                        if (!login && !status)
                        {
                            var count = Interlocked.Increment(ref sends);
                            if (count == 1) { firstSend.TrySetResult(); await release.Task.WaitAsync(stop.Token); }
                            else secondSend.TrySetResult();
                        }
                        var bytes = Encoding.UTF8.GetBytes(status ? JsonSerializer.Serialize(new { status = "ok", retcode = 0, data = new { online = !offlineHealth, good = true } }) :
                            login ? "{\"status\":\"ok\",\"retcode\":0,\"data\":{\"user_id\":99}}" : "{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":901}}");
                        await context.Response.OutputStream.WriteAsync(bytes, stop.Token); context.Response.Close();
                    }));
                }
            }
            catch (Exception error) when (stop.IsCancellationRequested && error is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
            finally { try { await Task.WhenAll(handlers); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { } }
        });
        Task? chat = null; var control = new AdminRuntimeControl();
        try
        {
            Environment.CurrentDirectory = fixture.Root; Environment.SetEnvironmentVariable("ONEBOT_ACCESS_TOKEN", "isolated-scheduler-runtime-token"); Console.SetOut(output);
            var settings = new GroupChatSettings { SelfId = 99, OneBotBaseUrl = fakeApi.Prefixes.Single(), EventPort = eventPort,
                AllowedGroupIds = [10], ActiveGroupId = 10, AllowedPrivateUserIds = [20] };
            File.WriteAllText(Path.Combine(fixture.Root, "group-chat.local.json"), JsonSerializer.Serialize(settings, BridgeConfig.JsonOptions));
            var contacts = new ContactContextStore(fixture.Root, () => fixture.Clock.Now);
            chat = GroupChatRuntime.RunAsync(settings, "10", 0, true, "fixed", stop.Token, fixture.Clock, control,
                contacts: contacts, scheduledMessages: fixture.Store);
            await Until(() => JsonSerializer.SerializeToElement(control.Snapshot()).GetProperty("running").GetBoolean());
            await Until(() => JsonSerializer.SerializeToElement(control.Snapshot(), BridgeConfig.JsonOptions).GetProperty("oneBot").GetProperty("state").GetString() == "offline");
            Check(!JsonSerializer.SerializeToElement(control.Snapshot(), BridgeConfig.JsonOptions).GetProperty("paused").GetBoolean(),
                "read-only offline health check paused or cleared live conversation state");
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }); http.DefaultRequestHeaders.Authorization = new("Bearer", "isolated-scheduler-runtime-token");
            var json = JsonSerializer.Serialize(new { post_type = "message", message_type = "group", self_id = 99, group_id = 10, user_id = 20,
                message_id = 42, time = fixture.Clock.Now.ToUnixTimeSeconds(), sender = new { user_id = 20 }, message = new[] { new { type = "text", data = new { text = "机器人你好" } } } });
            using var received = await http.PostAsync($"http://127.0.0.1:{eventPort}/onebot/", new StringContent(json, Encoding.UTF8, "application/json"));
            Check(received.IsSuccessStatusCode, "simulated incoming message rejected"); await firstSend.Task;
            fixture.Store.Save(new() { TargetId = 10, Enabled = true, OnceAt = fixture.Clock.Now.AddSeconds(1), Text = "SCHEDULE_SENTINEL" }, [10], [20]);
            fixture.Clock.Now = fixture.Clock.Now.AddSeconds(2);
            await Until(() => fixture.Store.List().Single().LastRun?.Status == "claimed");
            Check(sends == 1 && !secondSend.Task.IsCompleted, "scheduled HTTP ran in parallel with ordinary reply");
            release.TrySetResult(); await secondSend.Task;
            await Until(() => fixture.Store.List().Single().LastRun?.Status == "api_accepted");
            var snapshot = JsonSerializer.SerializeToElement(control.Snapshot(), BridgeConfig.JsonOptions);
            Check(snapshot.GetProperty("policy").GetProperty("groups")[0].GetProperty("contextCount").GetInt32() == 3 && !output.ToString().Contains("SCHEDULE_SENTINEL", StringComparison.Ordinal),
                "confirmed task missing from context or raw scheduled body leaked to logs");
            fixture.Store.Save(new() { TargetType = "private", TargetId = 20, Enabled = true,
                OnceAt = fixture.Clock.Now.AddSeconds(1), Text = "PRIVATE_SCHEDULE_SENTINEL" }, [10], [20]);
            fixture.Clock.Now = fixture.Clock.Now.AddSeconds(2);
            await Until(() => fixture.Store.List().Count == 2 && fixture.Store.List().All(task => task.LastRun?.Status == "api_accepted"));
            var archived = contacts.Recent(20).Single();
            snapshot = JsonSerializer.SerializeToElement(control.Snapshot(), BridgeConfig.JsonOptions);
            Check(archived.Role == "assistant" && archived.Text == "PRIVATE_SCHEDULE_SENTINEL" && sends == 3 &&
                snapshot.GetProperty("policy").GetProperty("privateContacts")[0].GetProperty("contextCount").GetInt32() == 1 &&
                snapshot.GetProperty("policy").GetProperty("groups")[0].GetProperty("contextCount").GetInt32() == 3 &&
                !output.ToString().Contains("PRIVATE_SCHEDULE_SENTINEL", StringComparison.Ordinal), "scheduled private archive/context crossed sessions or leaked to logs");
        }
        finally
        {
            stop.Cancel(); release.TrySetResult();
            try { if (chat is not null) await chat.WaitAsync(TimeSpan.FromSeconds(3)); await apiTask; }
            finally { Console.SetOut(beforeOutput); Environment.CurrentDirectory = beforeDirectory; Environment.SetEnvironmentVariable("ONEBOT_ACCESS_TOKEN", beforeToken); }
        }
        using var released = new FileStream(Path.Combine(fixture.Root, "Results/local/group-chat-session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check(released.CanWrite, "schedule owner retained chat lock after stop");
    }

    private static async Task Until(Func<bool> test)
    { var until = DateTime.UtcNow.AddSeconds(4); while (DateTime.UtcNow < until) { if (test()) return; await Task.Delay(15); } throw new Exception("schedule integration did not settle"); }
    private static int Port() { var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); var result = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop(); return result; }
    private static TaskCompletionSource Ready() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private sealed class Clock : TimeProvider
    {
        private long ticks = Start.UtcTicks;
        public DateTimeOffset Now { get => new(Interlocked.Read(ref ticks), TimeSpan.Zero); set => Interlocked.Exchange(ref ticks, value.UtcTicks); }
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly bool owns;
        private readonly string parent = Path.GetFullPath(Path.Combine("Cache", "scheduled-runtime-checks"));
        public string Root { get; }
        public Clock Clock { get; }
        public ScheduledMessageStore Store { get; }
        public MemeLibrary Memes { get; }
        public FakeApi Api { get; } = new();
        public OneBotMessageClient Client { get; }
        public ScheduledMessageScope Scope { get; set; } = new(99, [10], [20], false);
        public ScheduledMessageRateLimits Limits { get; }
        public int Confirmed; public bool AccountPaused;
        public Fixture(string? root = null, Clock? clock = null)
        {
            owns = root is null; Root = root ?? Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); Clock = clock ?? new();
            Store = new(Root, Clock); Memes = new(Root); if (owns) Memes.AddFace(14, ["惊讶"]);
            Client = new("http://127.0.0.1:3000/", "fake-scheduler-token", ["10"], Api, ["20"]); Limits = new(Clock);
        }
        public ScheduledMessageRuntime Runtime(bool live = true) => new(Store, Memes, Client, 99, live, () => Scope,
            (_, _, _) => Confirmed++, () => AccountPaused = true, Clock, Limits);
        public ScheduledMessageClaim Due(ScheduledMessageRuntime runtime, bool isPrivate = false, string? meme = "qq_face_14")
        {
            CreateDue(isPrivate, meme); return runtime.Poll(Start).Single();
        }
        public void CreateDue(bool isPrivate = false, string? meme = "qq_face_14")
        {
            Store.Save(new() { TargetType = isPrivate ? "private" : "group", TargetId = isPrivate ? 20 : 10, Enabled = true,
                OnceAt = Clock.Now.AddSeconds(1), Text = "固定合成内容", MemeId = meme }, [10], [20]);
            Clock.Now = Clock.Now.AddSeconds(2);
        }
        public void Dispose()
        {
            Client.Dispose(); if (!owns) return;
            if (!Path.GetFullPath(Root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("invalid test cleanup path");
            Directory.Delete(Root, true);
        }
    }
    private sealed class FakeApi : HttpMessageHandler
    {
        public int Calls, Sends, Logins; public long Account = 99; public bool BlockLogin, BlockSend, FailSend, FailStatus, OfflineAfterLogin;
        public bool Online = true, Good = true;
        public TaskCompletionSource LoginStarted { get; } = Ready();
        public TaskCompletionSource SendStarted { get; } = Ready();
        public TaskCompletionSource Release { get; } = Ready();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); var login = request.RequestUri!.AbsolutePath == "/get_login_info";
            if (request.RequestUri.AbsolutePath == "/get_status")
            {
                if (FailStatus) throw new HttpRequestException("simulated unavailable connection status");
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { status = "ok", retcode = 0, data = new { online = Online, good = Good } })) };
            }
            if (login)
            {
                Interlocked.Increment(ref Logins); LoginStarted.TrySetResult(); if (BlockLogin) await Release.Task.WaitAsync(ct);
                if (OfflineAfterLogin) Online = false;
            }
            else { Interlocked.Increment(ref Sends); SendStarted.TrySetResult(); if (BlockSend) await Release.Task.WaitAsync(ct); if (FailSend) throw new HttpRequestException("simulated unknown delivery"); }
            return new(HttpStatusCode.OK) { Content = new StringContent(login ? "{\"status\":\"ok\",\"retcode\":0,\"data\":{\"user_id\":" + Account + "}}" : "{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":901}}") };
        }
    }
}
