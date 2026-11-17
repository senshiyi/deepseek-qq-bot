using System.Net;
using System.Text;
using System.Text.Json;
using AudioBridge;

public static class MessageClientChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("structured text/face/image sends with Bearer and exact receipt", Send),
            ("quote replies put one reply segment first and reject other shapes", QuoteReply),
            ("private sends use the exact action, contact and validated segments", PrivateSend),
            ("private contact whitelist rejects before HTTP", PrivateScope),
            ("group and private whitelists remain independent", IndependentScopes),
            ("private failure receipts are uncertain and never retried", PrivateReceipts),
            ("friend request approval posts one exact action and tolerates a null data payload", FriendApproval),
            ("login requires a valid positive self id", Login),
            ("connection status requires explicit Boolean online and good flags", OnlineStatus),
            ("only loopback HTTP root origins are allowed", Origins),
            ("group whitelist rejects before HTTP", Scope),
            ("segment kinds, shapes, text and count limits reject before HTTP", Segments),
            ("image URLs, invalid bytes and oversize images reject before HTTP", Images),
            ("status, retcode and message id must confirm success", Receipts),
            ("redirect and HTTP failure never retry", HttpErrors),
            ("transport errors hide sensitive details and warn delivery is uncertain", Transport),
            ("inflight cancellation warns delivery is uncertain without retry", Cancellation),
            ("reply body is bounded", BodyLimit),
            ("five second deadline stops an unresponsive request", Deadline)
        };
        foreach (var check in checks) { await check.Run(); Console.WriteLine("PASS message client: " + check.Name); }
        return checks.Length;
    }

    private static async Task Send()
    {
        var calls = 0;
        var handler = new Handler(async (request, _) =>
        {
            calls++;
            Check(request.RequestUri?.ToString() == "http://127.0.0.1:3000/send_group_msg", "wrong action URL");
            Check(request.Headers.Authorization?.ToString() == "Bearer private-token", "missing Bearer");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = json.RootElement;
            Check(root.GetProperty("group_id").GetString() == "123" && root.GetProperty("auto_escape").GetBoolean(), "wrong group or CQ policy");
            var message = root.GetProperty("message");
            Check(message.ValueKind == JsonValueKind.Array && message.GetArrayLength() == 3, "message was not segment array");
            Check(message[0].GetProperty("data").GetProperty("text").GetString() == "[CQ:at,qq=all]你好", "literal text changed");
            Check(message[1].GetProperty("data").GetProperty("id").GetString() == "14", "face not normalized");
            Check(message[2].GetProperty("data").GetProperty("file").GetString()!.StartsWith("base64://", StringComparison.Ordinal), "image not inline");
            return Reply("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":-42}}");
        });
        using var client = Client(handler);
        var receipt = await client.SendGroupAsync(new("123", [OneBotSegment.Text("[CQ:at,qq=all]你好"), OneBotSegment.Face(14), OneBotSegment.Image(MemeLibraryChecks.Png)]), default);
        Check(receipt.MessageId == "-42" && calls == 1, "send receipt or count wrong");
    }

    private static async Task QuoteReply()
    {
        var handler = new Handler(async (request, _) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var message = json.RootElement.GetProperty("message");
            Check(message.GetArrayLength() == 2 && message[0].GetProperty("type").GetString() == "reply" &&
                message[0].GetProperty("data").GetProperty("id").GetString() == "-7" &&
                message[1].GetProperty("type").GetString() == "text", "quote reply was not serialized first");
            return Reply("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":11}}");
        });
        using var client = Client(handler);
        await client.SendGroupAsync(new("123", [OneBotSegment.Reply(-7), OneBotSegment.Text("收到")]), default);
        foreach (var segments in new OneBotSegment[][]
        {
            [OneBotSegment.Text("收到"), OneBotSegment.Reply(7)],
            [OneBotSegment.Reply(7), OneBotSegment.Reply(8), OneBotSegment.Text("收到")],
            [new OneBotSegment("reply", new { id = "abc" }), OneBotSegment.Text("收到")],
            [new OneBotSegment("reply", new { id = 7 }), OneBotSegment.Text("收到")],
            [new OneBotSegment("reply", new { qq = "7" }), OneBotSegment.Text("收到")]
        })
            await RejectAsync(() => client.SendGroupAsync(new("123", segments), default));
    }

    private static async Task FriendApproval()
    {
        var handler = new Handler(async (request, _) =>
        {
            Check(request.RequestUri?.ToString() == "http://127.0.0.1:3000/set_friend_add_request", "wrong friend request action");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(json.RootElement.GetProperty("flag").GetString() == "flag-7" && json.RootElement.GetProperty("approve").GetBoolean(),
                "friend request body changed");
            return Reply("{\"status\":\"ok\",\"retcode\":0,\"data\":null}");
        });
        using var client = Client(handler);
        await client.ApproveFriendRequestAsync("flag-7", default);
        Check(handler.Calls == 1, "friend approval was not sent once");
        foreach (var flag in new[] { "", " ", new string('x', 257), "a\0b" })
            await RejectAsync(() => client.ApproveFriendRequestAsync(flag, default));
        using var rejected = Client(new Handler((_, _) => Task.FromResult(Reply("{\"status\":\"failed\",\"retcode\":200,\"data\":null}"))));
        try { await rejected.ApproveFriendRequestAsync("flag-7", default); throw new Exception("failed friend approval was accepted"); }
        catch (OneBotMessageException) { }
    }

    private static async Task Login()
    {
        using var valid = Client(new Handler((_, _) => Task.FromResult(Reply("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"user_id\":456}}"))));
        Check(await valid.GetLoginInfoAsync(default) == "456", "login id wrong");
        foreach (var id in new[] { "0", "-1", "null", "\"x\"", "1.5" })
        {
            using var invalid = Client(new Handler((_, _) => Task.FromResult(Reply("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"user_id\":" + id + "}}"))));
            try { await invalid.GetLoginInfoAsync(default); throw new Exception("invalid login accepted"); }
            catch (OneBotMessageException error) { Check(!error.DeliveryMayHaveOccurred, "login claimed send"); }
        }
    }

    private static async Task OnlineStatus()
    {
        foreach (var online in new[] { false, true })
        foreach (var good in new[] { false, true })
        {
            using var valid = Client(new Handler((request, _) =>
            {
                Check(request.RequestUri!.AbsolutePath == "/get_status", "connection probe used cached login information");
                return Task.FromResult(Reply(JsonSerializer.Serialize(new { status = "ok", retcode = 0, data = new { online, good } })));
            }));
            Check(await valid.GetStatusAsync(default) == new OneBotOnlineStatus(online, good), "status flags changed");
        }
        foreach (var data in new[] { "{}", "{\"online\":true}", "{\"online\":\"true\",\"good\":true}", "{\"online\":true,\"good\":null}" })
        {
            using var invalid = Client(new Handler((_, _) => Task.FromResult(Reply("{\"status\":\"ok\",\"retcode\":0,\"data\":" + data + "}"))));
            try { await invalid.GetStatusAsync(default); throw new Exception("ambiguous status was accepted"); }
            catch (OneBotMessageException error) { Check(!error.DeliveryMayHaveOccurred, "read-only probe claimed delivery"); }
        }
    }

    private static async Task PrivateSend()
    {
        var handler = new Handler(async (request, _) =>
        {
            Check(request.RequestUri?.ToString() == "http://127.0.0.1:3000/send_private_msg", "wrong private action URL");
            Check(request.Headers.Authorization?.ToString() == "Bearer private-token", "missing private Bearer");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = json.RootElement;
            Check(root.GetProperty("user_id").GetString() == "456" && !root.TryGetProperty("group_id", out var unexpectedGroup) &&
                root.GetProperty("auto_escape").GetBoolean(), "wrong private target or CQ policy");
            var message = root.GetProperty("message");
            Check(message.ValueKind == JsonValueKind.Array && message.GetArrayLength() == 3, "private message was not segment array");
            Check(message[0].GetProperty("data").GetProperty("text").GetString() == "[CQ:at,qq=all]你好", "private literal text changed");
            Check(message[1].GetProperty("data").GetProperty("id").GetString() == "14", "private face not normalized");
            Check(message[2].GetProperty("data").GetProperty("file").GetString()!.StartsWith("base64://", StringComparison.Ordinal), "private image not inline");
            return Reply("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":-43}}");
        });
        using var client = Client(handler, ["456"]);
        var receipt = await client.SendPrivateAsync(new("00456", [OneBotSegment.Text("[CQ:at,qq=all]你好"), OneBotSegment.Face(14), OneBotSegment.Image(MemeLibraryChecks.Png)]), default);
        Check(receipt.MessageId == "-43" && handler.Calls == 1, "private receipt or count wrong");
    }

    private static async Task PrivateScope()
    {
        var handler = Never(); using var client = Client(handler, ["456"]);
        await RejectAsync(() => client.SendPrivateAsync(new("999", [OneBotSegment.Text("hello")]), default));
        await RejectAsync(() => client.SendPrivateAsync(new("../../456", [OneBotSegment.Text("hello")]), default));
        await RejectAsync(() => client.SendPrivateAsync(new("0", [OneBotSegment.Text("hello")]), default));
        Check(handler.Calls == 0, "rejected contact reached HTTP");
    }

    private static async Task IndependentScopes()
    {
        var handler = Never();
        using (var client = Client(handler))
            await RejectAsync(() => client.SendPrivateAsync(new("123", [OneBotSegment.Text("hello")]), default));
        using (var client = Client(handler, ["456"]))
        {
            await RejectAsync(() => client.SendPrivateAsync(new("123", [OneBotSegment.Text("hello")]), default));
            await RejectAsync(() => client.SendGroupAsync(new("456", [OneBotSegment.Text("hello")]), default));
        }
        Check(handler.Calls == 0, "one whitelist authorized the other destination type");
    }

    private static async Task PrivateReceipts()
    {
        foreach (var json in new[]
        {
            "{\"status\":\"failed\",\"retcode\":1,\"data\":{\"message_id\":1}}",
            "{\"status\":\"ok\",\"retcode\":0,\"data\":{}}",
            "{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":0}}",
            "not json"
        })
        {
            var handler = new Handler((_, _) => Task.FromResult(Reply(json))); using var client = Client(handler, ["456"]);
            await Uncertain(client.SendPrivateAsync(new("456", [OneBotSegment.Text("hello")]), default));
            Check(handler.Calls == 1, "invalid private receipt retried");
        }
    }

    private static Task Origins()
    {
        foreach (var url in new[] { "https://127.0.0.1:3000", "http://192.168.1.2:3000", "http://example.com", "http://127.0.0.1/a", "http://u:p@127.0.0.1", "http://127.0.0.1/?token=x", "http://127.0.0.1/#x", "file:///x" })
            Reject(() => { using var client = new OneBotMessageClient(url, "private-token", ["123"]); });
        using var localhost = new OneBotMessageClient("http://localhost:3000", "private-token", ["123"]);
        using var ipv6 = new OneBotMessageClient("http://[::1]:3000", "private-token", ["123"]);
        Reject(() => { using var client = new OneBotMessageClient("http://127.0.0.1:3000", "bad\r\ntoken", ["123"]); });
        return Task.CompletedTask;
    }

    private static async Task Scope()
    {
        var handler = Never(); using var client = Client(handler);
        await RejectAsync(() => client.SendGroupAsync(new("999", [OneBotSegment.Text("hello")]), default));
        await RejectAsync(() => client.SendGroupAsync(new("../../123", [OneBotSegment.Text("hello")]), default));
        Check(handler.Calls == 0, "rejected group reached HTTP");
    }

    private static async Task Segments()
    {
        var handler = Never(); using var client = Client(handler, ["456"]);
        var invalid = new IReadOnlyList<OneBotSegment>[]
        {
            [], Enumerable.Repeat(OneBotSegment.Text("x"), 5).ToArray(),
            [OneBotSegment.Text(new string('x', 3001))], [OneBotSegment.Text(new string('x', 1501)), OneBotSegment.Text(new string('x', 1500))],
            [new("at", new { qq = "all" })], [new("text", "[CQ:image,file=x]")], [new("text", new { text = "x", extra = "x" })],
            [OneBotSegment.Face(-1)], [new("face", new { id = 1.5 })], [OneBotSegment.Text("")]
        };
        foreach (var segments in invalid)
        {
            await RejectAsync(() => client.SendGroupAsync(new("123", segments), default));
            await RejectAsync(() => client.SendPrivateAsync(new("456", segments), default));
        }
        Check(handler.Calls == 0, "invalid segment reached HTTP");
    }

    private static async Task Images()
    {
        var handler = Never(); using var client = Client(handler, ["456"]);
        foreach (var file in new[] { "https://example.com/x.png", "file:///C:/x.png", "C:\\x.png", "base64://%%%", "base64://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("not a real image")) })
        {
            await RejectAsync(() => client.SendGroupAsync(new("123", [new("image", new { file })]), default));
            await RejectAsync(() => client.SendPrivateAsync(new("456", [new("image", new { file })]), default));
        }
        Reject(() => OneBotSegment.Image(new byte[OneBotMessageClient.MaximumImageBytes + 1]));
        Check(handler.Calls == 0, "invalid image reached HTTP");
    }

    private static async Task Receipts()
    {
        foreach (var json in new[]
        {
            "{}", "[]", "not json", "{\"status\":\"failed\",\"retcode\":0,\"data\":{\"message_id\":1}}",
            "{\"status\":\"async\",\"retcode\":1,\"data\":{\"message_id\":1}}",
            "{\"status\":\"ok\",\"retcode\":\"0\",\"data\":{\"message_id\":1}}",
            "{\"status\":\"ok\",\"retcode\":0,\"data\":{}}", "{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":0}}",
            "{\"status\":\"failed\",\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":1}}",
            "{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":1.25}}"
        })
        {
            var handler = new Handler((_, _) => Task.FromResult(Reply(json))); using var client = Client(handler);
            await Uncertain(client.SendGroupAsync(Message(), default)); Check(handler.Calls == 1, "invalid receipt retried");
        }
    }

    private static async Task HttpErrors()
    {
        foreach (var code in new[] { HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError })
        {
            var handler = new Handler((_, _) =>
            {
                var response = new HttpResponseMessage(code);
                if (code == HttpStatusCode.Redirect) response.Headers.Location = new Uri("https://example.com/private-token");
                return Task.FromResult(response);
            });
            using var client = Client(handler); await Uncertain(client.SendGroupAsync(Message(), default));
            Check(handler.Calls == 1, "HTTP error followed redirect or retried");
        }
    }

    private static async Task Transport()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("private-token secret-body http://secret.example"));
        using var client = Client(handler); await Uncertain(client.SendGroupAsync(Message(), default));
        Check(handler.Calls == 1, "transport error retried");
    }

    private static async Task Cancellation()
    {
        using var stopping = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Reply("{}"); });
        using var client = Client(handler); var send = client.SendGroupAsync(Message(), stopping.Token);
        await entered.Task; stopping.Cancel(); await Uncertain(send); Check(handler.Calls == 1, "cancelled send retried");
        var before = handler.Calls;
        try { await client.SendGroupAsync(Message(), stopping.Token); throw new Exception("pre-cancelled send accepted"); }
        catch (OperationCanceledException) { }
        Check(handler.Calls == before, "pre-cancelled send opened HTTP");
    }

    private static async Task BodyLimit()
    {
        using var client = Client(new Handler((_, _) => Task.FromResult(Reply(new string('x', 65537)))));
        await Uncertain(client.SendGroupAsync(Message(), default));
    }

    private static async Task Deadline()
    {
        var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Reply("{}"); });
        using var client = Client(handler);
        await Uncertain(client.SendGroupAsync(Message(), default)).WaitAsync(TimeSpan.FromSeconds(7));
        Check(handler.Calls == 1, "timed-out send retried");
    }

    private static async Task Uncertain(Task task)
    {
        try { await task; throw new Exception("failure accepted as success"); }
        catch (OneBotMessageException error)
        {
            Check(error.DeliveryMayHaveOccurred && error.Message.Contains("不能保证消息未送达", StringComparison.Ordinal), "uncertainty omitted");
            Check(error.InnerException is null && !error.ToString().Contains("private-token", StringComparison.Ordinal) &&
                !error.ToString().Contains("secret-body", StringComparison.Ordinal) && !error.ToString().Contains("http://", StringComparison.Ordinal), "sensitive exception leaked");
        }
    }

    private static OneBotMessageClient Client(Handler handler, IEnumerable<string>? privateUsers = null) =>
        new("http://127.0.0.1:3000", "private-token", ["123"], handler, privateUsers);
    private static Handler Never() => new((_, _) => throw new Exception("unexpected HTTP request"));
    private static OutgoingGroupMessage Message() => new("123", [OneBotSegment.Text("hello")]);
    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Reject(Action operation) { try { operation(); } catch (Exception error) when (error is ArgumentException or InvalidDataException) { return; } throw new Exception("invalid input accepted"); }
    private static async Task RejectAsync(Func<Task> operation) { try { await operation(); } catch (Exception error) when (error is ArgumentException or InvalidDataException) { return; } throw new Exception("invalid input accepted"); }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return action(request, ct); }
    }
}
