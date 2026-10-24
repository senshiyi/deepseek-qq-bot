using System.Net;
using System.Text;
using System.Text.Json;
using AudioBridge;

public static class SchoolApiChecks
{
    private const string Decision = "{\"send\":true,\"text\":\"收到\",\"memeId\":null}";
    private const string Key = "fixture-school-key";
    private static SchoolModelSettings Settings => new() { Model = "fixture-model", InputPointsPerMillion = 1, OutputPointsPerMillion = 1, MaxPoints = 1, MaxCalls = 100 };

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("unknown prices/model and untrusted endpoints fail locally", Configuration),
            ("request contains only documented text fields and no vendor extensions", Request),
            ("nonstream content excludes reasoning", Nonstream),
            ("standard SSE validates stop and DONE and excludes reasoning", Streaming),
            ("3000-character JSON replies survive complete and chunked responses at the output ceiling", LongOutput),
            ("nonstream truncated/tool/malformed responses are rejected", InvalidNonstream),
            ("SSE missing end/stop, tool calls and late text are rejected", InvalidStreaming),
            ("point reservation uses configured rates and retains totals after cap changes", PointAccounting),
            ("unlimited caps retain existing spend and continue beyond ten calls and old point cap", UnlimitedBudget),
            ("point and call caps can be disabled independently", IndependentCaps),
            ("independent budget instances retain concurrent reservations", ConcurrentBudget),
            ("explicit zero rates permit zero point budget with bounded calls", FreeBudget),
            ("invalid or foreign budget ledger is never reset", InvalidLedger),
            ("pre-cancelled request neither calls HTTP nor reserves", PreCancelled),
            ("inflight cancellation retains spend and rejects a late response", Cancellation),
            ("overall timeout rejects late transport without retry", Timeout),
            ("HTTP/redirect/transport errors hide credentials and preserve spend", Errors),
            ("declared and streamed response size are bounded", BodyLimit),
            ("handler forbids redirects/cookies while preserving system proxy", HandlerOptions),
            ("vision turn sends image blocks only when images are provided", VisionTurn),
            ("vision caption uses the vision model and rejects empty descriptions", VisionCaption)
        };
        foreach (var check in checks) { await check.Run(); Console.WriteLine("PASS school API: " + check.Name); }
        return checks.Length;
    }

    private static Task Configuration()
    {
        using var folder = new Folder();
        foreach (var settings in new[] { new SchoolModelSettings(), Settings with { Model = "" }, Settings with { InputPointsPerMillion = null },
            Settings with { OutputPointsPerMillion = -1 }, Settings with { MaxPoints = -1 }, Settings with { MaxPoints = 0 }, Settings with { MaxCalls = 1001 },
            Settings with { MaxCalls = 0 }, Settings with { MaxPoints = null, MaxCalls = null, InputPointsPerMillion = null },
            Settings with { MaxPoints = null, MaxCalls = null, OutputPointsPerMillion = null } })
            Reject(() => new SchoolUsageBudget(folder.PathOf("absent.json"), settings));
        foreach (var url in new[] { "http://myai.bupt.edu.cn/llm-gw/v1/", "https://example.com/llm-gw/v1/", "https://127.0.0.1/llm-gw/v1/",
            "https://key@myai.bupt.edu.cn/llm-gw/v1/", "https://myai.bupt.edu.cn:444/llm-gw/v1/", "https://myai.bupt.edu.cn/llm-gw/v1/?key=x", "https://myai.bupt.edu.cn/wrong/" })
            Reject(() => (Settings with { BaseUrl = url }).Validate());
        Check(!File.Exists(folder.PathOf("absent.json")), "invalid configuration created ledger");
        File.WriteAllText(folder.PathOf("settings.json"), JsonSerializer.Serialize(Settings));
        Check(SchoolModelSettings.Load(folder.PathOf("settings.json")).Model == "fixture-model", "settings did not load");
        File.WriteAllText(folder.PathOf("settings.json"), JsonSerializer.Serialize(Settings with { MaxPoints = null, MaxCalls = null }));
        var unlimited = SchoolModelSettings.Load(folder.PathOf("settings.json"));
        Check(unlimited.MaxPoints is null && unlimited.MaxCalls is null, "explicit null caps did not load");
        return Task.CompletedTask;
    }

    private static async Task Request()
    {
        using var folder = new Folder(); var settings = Settings;
        var handler = new Fake(async (request, _) =>
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri?.ToString() == "https://myai.bupt.edu.cn/llm-gw/v1/chat/completions", "wrong endpoint");
            Check(request.Headers.Authorization?.ToString() == "Bearer " + Key, "missing Bearer");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = json.RootElement;
            Check(root.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(new[] { "max_tokens", "messages", "model", "stream" }), "undocumented request fields");
            Check(root.GetProperty("max_tokens").GetInt32() == 256 && !root.GetProperty("stream").GetBoolean(), "wrong output bound or default stream");
            var messages = root.GetProperty("messages");
            Check(messages[0].GetProperty("content").GetString()!.Contains("没有可读的图片", StringComparison.Ordinal), "text-only rule missing");
            Check(messages.EnumerateArray().All(x => x.GetProperty("content").ValueKind == JsonValueKind.String), "image content sent without a vision turn");
            return JsonReply();
        });
        using var reply = Source(folder, settings, handler);
        Check(await Collect(reply) == Decision && handler.Calls == 1, "request failed");
    }

    private static async Task Nonstream()
    {
        using var folder = new Folder(); var handler = new Fake((_, _) => Task.FromResult(JsonReply()));
        using var reply = Source(folder, Settings, handler);
        Check(await Collect(reply) == Decision && reply.ObservedModel == "fixture-observed", "nonstream text/model wrong");
    }

    private static async Task Streaming()
    {
        using var folder = new Folder();
        var body = Frame(new { role = "assistant", reasoning_content = "private-reasoning" }) + Frame(new { content = Decision }) +
            Frame(new { }, "stop") + "data: {\"choices\":[],\"usage\":{\"total_tokens\":2}}\n\ndata: [DONE]\n\n";
        var handler = new Fake((_, _) => Task.FromResult(Response(body, "text/event-stream")));
        using var reply = Source(folder, Settings with { Stream = true }, handler);
        Check(await Collect(reply) == Decision, "SSE text included reasoning or lost content");
    }

    private static async Task LongOutput()
    {
        var decision = JsonSerializer.Serialize(new { send = true, text = new string('中', 3000), memeId = (string?)null });
        foreach (var stream in new[] { false, true })
        {
            using var folder = new Folder(); var settings = Settings with { Stream = stream };
            var budget = new SchoolUsageBudget(folder.Ledger, settings);
            var handler = new Fake(async (request, ct) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Check(body.RootElement.GetProperty("max_tokens").GetInt32() == 8192, "long school response did not request the configured output ceiling");
                var response = stream
                    ? Frame(new { content = decision[..(decision.Length / 2)] }) + Frame(new { content = decision[(decision.Length / 2)..] }) + Frame(new { }, "stop") + "data: [DONE]\n\n"
                    : JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = decision } } } });
                return Response(response, stream ? "text/event-stream" : "application/json");
            });
            using var reply = new SchoolModelReply(settings, Key, budget, "Reply strict JSON", handler, maximumOutputTokens: 8192);
            Check(await Collect(reply) == decision && handler.Calls == 1, "long school JSON content was truncated, lost or retried");
            Check(Snapshot(budget).GetProperty("calls").GetInt32() == 1, "long school request lost its reservation");
        }
    }

    private static async Task InvalidNonstream()
    {
        foreach (var body in new[]
        {
            "not json", "[]", "{\"choices\":[]}",
            "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}",
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"reasoning_content\":\"hidden\"}}]}",
            "{\"choices\":[{\"finish_reason\":\"length\",\"finish_reason\":\"stop\",\"message\":{\"content\":\"partial\"}}]}",
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"x\",\"tool_calls\":[]}}]}"
        })
        {
            using var folder = new Folder(); using var reply = Source(folder, Settings, new Fake((_, _) => Task.FromResult(Response(body, "application/json"))));
            await Fails(() => Collect(reply));
        }
    }

    private static async Task InvalidStreaming()
    {
        foreach (var body in new[]
        {
            Frame(new { content = "partial" }) + Frame(new { }, "stop"),
            Frame(new { content = "partial" }) + "data: [DONE]\n\n",
            Frame(new { content = "partial" }, "length") + "data: [DONE]\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"length\",\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
            Frame(new { tool_calls = new[] { new { id = "tool" } } }) + "data: [DONE]\n\n",
            Frame(new { content = "x" }, "stop") + Frame(new { content = "late" }) + "data: [DONE]\n\n",
            Frame(new { reasoning_content = "private" }, "stop") + "data: [DONE]\n\n"
        })
        {
            using var folder = new Folder(); using var reply = Source(folder, Settings with { Stream = true }, new Fake((_, _) => Task.FromResult(Response(body, "text/event-stream"))));
            await Fails(() => Collect(reply));
        }
    }

    private static Task PointAccounting()
    {
        using var folder = new Folder(); var settings = Settings with { OutputPointsPerMillion = 2 };
        var budget = new SchoolUsageBudget(folder.Ledger, settings);
        budget.Reserve([new("user", "你好")]);
        Check(Snapshot(budget).GetProperty("reservedPoints").GetDecimal() == .004742m, "point rates or token ceiling wrong");
        var lower = new SchoolUsageBudget(folder.Ledger, settings with { MaxPoints = .001m });
        Reject(() => lower.Reserve([new("user", "你好")]));
        Check(Snapshot(lower).GetProperty("reservedPoints").GetDecimal() == .004742m && Snapshot(lower).GetProperty("calls").GetInt32() == 1, "lower cap reset spend");
        return Task.CompletedTask;
    }

    private static async Task UnlimitedBudget()
    {
        using var folder = new Folder();
        var boundedSettings = Settings with { MaxPoints = .10m, MaxCalls = 10 };
        var bounded = new SchoolUsageBudget(folder.Ledger, boundedSettings);
        var history = new[] { new ChatTurn("user", "x") };
        for (var i = 0; i < 10; i++) bounded.Reserve(history);
        Reject(() => bounded.Reserve(history));
        var previous = Snapshot(bounded);
        Check(previous.GetProperty("calls").GetInt32() == 10, "bounded call limit changed");

        var settings = boundedSettings with { MaxPoints = null, MaxCalls = null };
        var first = new SchoolUsageBudget(folder.Ledger, settings);
        Check(Snapshot(first).GetProperty("reservedPoints").GetDecimal() == previous.GetProperty("reservedPoints").GetDecimal(), "unlimited mode reset existing spend");
        var second = new SchoolUsageBudget(folder.Ledger, settings);
        await Task.WhenAll(new[] { first, second }.Select(budget => Task.Run(() => { for (var i = 0; i < 10; i++) budget.Reserve(history); })));
        var current = Snapshot(new SchoolUsageBudget(folder.Ledger, settings));
        Check(current.GetProperty("calls").GetInt32() == 30 && current.GetProperty("reservedPoints").GetDecimal() == .13443m,
            "unlimited mode failed beyond previous caps or lost concurrent spend");
        Check(current.GetProperty("maxPoints").ValueKind == JsonValueKind.Null && current.GetProperty("maxCalls").ValueKind == JsonValueKind.Null,
            "snapshot did not distinguish unlimited caps");
    }

    private static Task IndependentCaps()
    {
        using var callsFolder = new Folder();
        var callBounded = new SchoolUsageBudget(callsFolder.Ledger, Settings with { MaxPoints = null, MaxCalls = 2 });
        var history = new[] { new ChatTurn("user", "x") };
        callBounded.Reserve(history); callBounded.Reserve(history); Reject(() => callBounded.Reserve(history));
        Check(Snapshot(callBounded).GetProperty("calls").GetInt32() == 2, "call cap ignored when point cap disabled");

        using var pointsFolder = new Folder();
        var pointBounded = new SchoolUsageBudget(pointsFolder.Ledger, Settings with { MaxPoints = .004481m, MaxCalls = null });
        pointBounded.Reserve(history); Reject(() => pointBounded.Reserve(history));
        Check(Snapshot(pointBounded).GetProperty("reservedPoints").GetDecimal() == .004481m && Snapshot(pointBounded).GetProperty("calls").GetInt32() == 1,
            "point cap ignored when call cap disabled");
        return Task.CompletedTask;
    }

    private static async Task ConcurrentBudget()
    {
        using var folder = new Folder(); var first = new SchoolUsageBudget(folder.Ledger, Settings); var second = new SchoolUsageBudget(folder.Ledger, Settings);
        var history = new[] { new ChatTurn("user", "x") };
        await Task.WhenAll(new[] { first, second }.Select(budget => Task.Run(() => { for (var i = 0; i < 10; i++) budget.Reserve(history); })));
        var current = Snapshot(new SchoolUsageBudget(folder.Ledger, Settings));
        Check(current.GetProperty("calls").GetInt32() == 20 && current.GetProperty("reservedPoints").GetDecimal() == .08962m, "cross-instance budget update lost");
    }

    private static Task FreeBudget()
    {
        using var folder = new Folder(); var settings = Settings with { InputPointsPerMillion = 0, OutputPointsPerMillion = 0, MaxPoints = 0, MaxCalls = 2 };
        var budget = new SchoolUsageBudget(folder.Ledger, settings);
        budget.Reserve([]); budget.Reserve([]); Reject(() => budget.Reserve([]));
        Check(Snapshot(budget).GetProperty("calls").GetInt32() == 2 && Snapshot(budget).GetProperty("reservedPoints").GetDecimal() == 0, "zero-price call cap wrong");
        return Task.CompletedTask;
    }

    private static Task InvalidLedger()
    {
        using var folder = new Folder();
        foreach (var content in new[] { "not json", "{\"reservedYuan\":0,\"calls\":0}", "{\"version\":1,\"reservedPoints\":-1,\"calls\":0}" })
        {
            File.WriteAllText(folder.Ledger, content); Reject(() => new SchoolUsageBudget(folder.Ledger, Settings));
            Check(File.ReadAllText(folder.Ledger) == content, "invalid ledger was reset");
        }
        return Task.CompletedTask;
    }

    private static async Task PreCancelled()
    {
        using var folder = new Folder(); var budget = new SchoolUsageBudget(folder.Ledger, Settings); var handler = new Fake((_, _) => throw new Exception("unexpected HTTP"));
        using var reply = new SchoolModelReply(Settings, Key, budget, "Reply JSON", handler);
        using var stopping = new CancellationTokenSource(); stopping.Cancel();
        await Fails(() => Collect(reply, stopping.Token));
        Check(handler.Calls == 0 && Snapshot(budget).GetProperty("calls").GetInt32() == 0, "pre-cancelled work sent or charged");
    }

    private static async Task Cancellation()
    {
        using var folder = new Folder(); using var stopping = new CancellationTokenSource();
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Fake((_, _) => { entered.SetResult(); return late.Task; });
        var budget = new SchoolUsageBudget(folder.Ledger, Settings);
        using var reply = new SchoolModelReply(Settings, Key, budget, "Reply JSON", handler);
        var active = Collect(reply, stopping.Token); await entered.Task; stopping.Cancel(); await Fails(() => active);
        late.SetResult(JsonReply()); await Task.Delay(10);
        Check(handler.Calls == 1 && Snapshot(budget).GetProperty("calls").GetInt32() == 1, "cancelled request retried or refunded");
    }

    private static async Task Timeout()
    {
        using var folder = new Folder(); var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Fake((_, _) => late.Task);
        var budget = new SchoolUsageBudget(folder.Ledger, Settings);
        using var reply = new SchoolModelReply(Settings, Key, budget, "Reply JSON", handler, TimeSpan.FromMilliseconds(30));
        await Fails(() => Collect(reply).WaitAsync(TimeSpan.FromSeconds(2)));
        late.SetResult(JsonReply()); await Task.Delay(10);
        Check(handler.Calls == 1 && Snapshot(budget).GetProperty("calls").GetInt32() == 1, "timeout retried or refunded");
    }

    private static async Task Errors()
    {
        foreach (var code in new[] { HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError })
        {
            using var folder = new Folder(); var budget = new SchoolUsageBudget(folder.Ledger, Settings);
            var handler = new Fake((_, _) => { var response = new HttpResponseMessage(code) { Content = new StringContent(Key + " secret-body") }; response.Headers.Location = new Uri("https://secret.example/" + Key); return Task.FromResult(response); });
            using var reply = new SchoolModelReply(Settings, Key, budget, "Reply JSON", handler);
            await Fails(() => Collect(reply)); Check(handler.Calls == 1 && Snapshot(budget).GetProperty("calls").GetInt32() == 1, "error retried or refunded");
        }
        using var transportFolder = new Folder();
        using var transport = Source(transportFolder, Settings, new Fake((_, _) => throw new HttpRequestException(Key + " secret-body https://secret.example")));
        await Fails(() => Collect(transport));
    }

    private static async Task BodyLimit()
    {
        foreach (var knownLength in new[] { true, false })
        {
            using var folder = new Folder();
            var handler = new Fake((_, _) =>
            {
                HttpContent content = knownLength ? new ByteArrayContent(new byte[SchoolModelReply.MaximumResponseBytes + 1]) :
                    new StreamContent(new NonSeekable(new byte[SchoolModelReply.MaximumResponseBytes + 1]));
                content.Headers.ContentType = new("application/json");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });
            using var reply = Source(folder, Settings, handler); await Fails(() => Collect(reply));
        }
    }

    private static async Task VisionTurn()
    {
        using var folder = new Folder();
        var images = new[] { new InlineImage("image/png", "data:image/png;base64,AAAA") };
        var handler = new Fake(async (request, _) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = json.RootElement;
            Check(root.GetProperty("model").GetString() == "fixture-model", "vision turn changed the text model");
            var messages = root.GetProperty("messages");
            Check(messages[0].GetProperty("content").GetString()!.Contains("本轮带有图片", StringComparison.Ordinal), "image turn note missing");
            var blocks = messages[messages.GetArrayLength() - 1].GetProperty("content");
            Check(blocks.ValueKind == JsonValueKind.Array && blocks.GetArrayLength() == 2, "image blocks missing or duplicated");
            Check(blocks[1].GetProperty("image_url").GetProperty("url").GetString() == "data:image/png;base64,AAAA", "inline image changed");
            return JsonReply();
        });
        using var reply = new SchoolModelReply(Settings, Key, new SchoolUsageBudget(folder.Ledger, Settings), "Reply strict JSON",
            handler, images: images);
        Check(await Collect(reply) == Decision && handler.Calls == 1, "image turn did not reach transport");
        var spent = Snapshot(new SchoolUsageBudget(folder.Ledger, Settings)).GetProperty("reservedPoints").GetDecimal();
        Check(spent > 0, "image turn was not charged");
    }

    private static async Task VisionCaption()
    {
        using var folder = new Folder();
        var vision = Settings with { VisionModel = "fixture-vision" };
        var handler = new Fake(async (request, _) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = json.RootElement;
            Check(root.GetProperty("model").GetString() == "fixture-vision", "caption request did not use the vision model");
            Check(root.GetProperty("max_tokens").GetInt32() == SchoolVision.CaptionOutputTokens, "caption token bound changed");
            Check(root.GetProperty("messages")[1].GetProperty("content").ValueKind == JsonValueKind.Array, "caption request carried no image");
            return Response(JsonSerializer.Serialize(new { model = "fixture-vision", choices = new[] {
                new { finish_reason = "stop", message = new { content = "  左边红色圆形\n右边蓝色正方形  " } } } }), "application/json");
        });
        var captioner = new SchoolVision(vision, Key, new SchoolUsageBudget(folder.Ledger, vision), handler);
        var caption = await captioner.DescribeAsync("这是什么", [new InlineImage("image/png", "data:image/png;base64,AAAA")], default);
        Check(caption == "左边红色圆形 右边蓝色正方形" && handler.Calls == 1, "caption was not collapsed or requested once");
        var empty = new SchoolVision(vision, Key, new SchoolUsageBudget(folder.Ledger, vision),
            new Fake((_, _) => Task.FromResult(Response(JsonSerializer.Serialize(new { model = "fixture-vision", choices = new[] {
                new { finish_reason = "stop", message = new { content = "   " } } } }), "application/json"))));
        try { await empty.DescribeAsync("这是什么", [new InlineImage("image/png", "data:image/png;base64,AAAA")], default); throw new Exception("empty caption accepted"); }
        catch (InvalidDataException) { }
        // 思考占满额度时正文会以 length 结束：转述接受截断，但空正文仍然拒绝。
        var truncated = new SchoolVision(vision, Key, new SchoolUsageBudget(folder.Ledger, vision),
            new Fake((_, _) => Task.FromResult(Response(JsonSerializer.Serialize(new { model = "fixture-vision", choices = new[] {
                new { finish_reason = "length", message = new { content = "左边红色圆形" } } } }), "application/json"))));
        var cut = await truncated.DescribeAsync("这是什么", [new InlineImage("image/png", "data:image/png;base64,AAAA")], default);
        Check(cut == "左边红色圆形", "a truncated caption was rejected or altered");
        var disabled = new SchoolVision(Settings, Key, new SchoolUsageBudget(folder.Ledger, Settings));
        Check(!disabled.IsEnabled, "vision model defaulted on");
        try { await disabled.DescribeAsync("这是什么", [new InlineImage("image/png", "data:image/png;base64,AAAA")], default); throw new Exception("unconfigured vision model was called"); }
        catch (InvalidOperationException) { }
    }

    private static Task HandlerOptions()
    {
        using var folder = new Folder(); var handler = new HttpClientHandler { UseProxy = true };
        using var reply = new SchoolModelReply(Settings, Key, new SchoolUsageBudget(folder.Ledger, Settings), "Reply JSON", handler);
        Check(handler.UseProxy && !handler.AllowAutoRedirect && !handler.UseCookies, "proxy/redirect/cookie configuration wrong");
        return Task.CompletedTask;
    }

    private static SchoolModelReply Source(Folder folder, SchoolModelSettings settings, HttpMessageHandler handler) => new(settings, Key, new SchoolUsageBudget(folder.Ledger, settings), "Reply strict JSON", handler);
    private static JsonElement Snapshot(SchoolUsageBudget budget) => JsonSerializer.SerializeToElement(budget.Snapshot());
    private static async Task<string> Collect(IStreamingReply source, CancellationToken ct = default) { var body = new StringBuilder(); await foreach (var text in source.StreamAsync([new("user", "hello")], ct)) body.Append(text); return body.ToString(); }
    private static HttpResponseMessage JsonReply() => Response(JsonSerializer.Serialize(new { model = "fixture-observed", choices = new[] { new { finish_reason = "stop", message = new { content = Decision, reasoning_content = "private-reasoning" } } } }), "application/json");
    private static HttpResponseMessage Response(string body, string type) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
    private static string Frame(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta, finish_reason = finish } } }) + "\n\n";
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException) { return; } throw new Exception("invalid school configuration/accounting accepted"); }
    private static async Task Fails(Func<Task> action)
    {
        try { await action(); } catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or JsonException)
        { Check(!e.ToString().Contains(Key, StringComparison.Ordinal) && !e.ToString().Contains("secret-body", StringComparison.Ordinal) && !e.ToString().Contains("https://secret.example", StringComparison.Ordinal), "school error leaked credentials/body/URL"); return; }
        throw new Exception("invalid school response accepted");
    }

    private sealed class Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { public int Calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Interlocked.Increment(ref Calls); return callback(request, ct); } }
    private sealed class NonSeekable(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class Folder : IDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine("Cache", "school-api-checks", Guid.NewGuid().ToString("N")));
        public Folder() => Directory.CreateDirectory(root);
        public string Ledger => PathOf("school-api-budget.json"); public string PathOf(string name) => Path.Combine(root, name);
        public void Dispose() { var parent = Path.GetFullPath(Path.Combine("Cache", "school-api-checks")) + Path.DirectorySeparatorChar; if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(); Directory.Delete(root, true); }
    }
}
