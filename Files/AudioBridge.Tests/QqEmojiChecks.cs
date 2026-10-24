using System.Net;
using System.Text;
using System.Text.Json;
using AudioBridge;

public static class QqEmojiChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("only fixed loopback account API accepts credentials", Configuration),
            ("favorites are opaque selections and validated image bytes", Selection),
            ("unsafe external URLs are rejected before download", UrlScope),
            ("exact QQ multimedia CDN permits bounded images without permitting adjacent domains", ExactQqCdn),
            ("missing, stale and expired selections never download", Catalog),
            ("account changes invalidate selection and returned bytes", AccountChange),
            ("HTTP redirects, oversized and malformed content are rejected", Downloads),
            ("cancellation stops lookup and download without leaking transport detail", Cancellation),
            ("array shape, item limits and API status are validated", ApiShape)
        };
        foreach (var check in checks) { await check.Run(); Console.WriteLine("PASS QQ favorites: " + check.Name); }
        return checks.Length;
    }

    private static Task Configuration()
    {
        foreach (var url in new[] { "https://127.0.0.1:3000/", "http://example.com/", "http://127.0.0.1:3000/x", "http://user@127.0.0.1:3000/" })
            Reject(() => new QqEmojiClient(url, "test-token", 1));
        Reject(() => new QqEmojiClient("http://127.0.0.1:3000/", "bad\r\ntoken", 1));
        Reject(() => new QqEmojiClient("http://127.0.0.1:3000/", "test-token", 0));
        return Task.CompletedTask;
    }

    private static async Task Selection()
    {
        var api = new Api(); var images = new Images();
        using var client = Client(api, images);
        var list = await client.ListAsync(default);
        Check(list.Count == 1 && list[0].Id.Length == 64 && !JsonSerializer.Serialize(list).Contains("qpic.cn"), "list exposed an image URL");
        var asset = await client.ReadAsync(list[0].Id, default);
        Check(asset.Content.SequenceEqual(MemeLibraryChecks.Png) && asset.MediaType == "image/png", "image bytes changed");
        Check(images.Calls == 1 && !images.HadAuthorization && api.Actions.SequenceEqual(new[] { "get_login_info", "fetch_custom_face", "get_login_info", "get_login_info", "get_login_info" }), "unexpected API or credential leakage");
    }

    private static async Task UrlScope()
    {
        foreach (var url in new[] { "http://p.qpic.cn/x", "https://p.qpic.cn.evil.test/x", "https://evilqpic.cn/x", "https://127.0.0.1/x", "https://p.qpic.cn:444/x", "https://u@p.qpic.cn/x", "https://p.qpic.cn/x#secret", "file:///tmp/a.png",
                     "http://multimedia.nt.qq.com.cn/x", "https://multimedia.nt.qq.com.cn:444/x", "https://sub.multimedia.nt.qq.com.cn/x",
                     "https://multimedia.nt.qq.com.cn.evil.test/x", "https://evilqq.com.cn/x", "https://u@multimedia.nt.qq.com.cn/x" })
        {
            var api = new Api { Urls = [url] }; var images = new Images(); using var client = Client(api, images);
            await RejectAsync(() => client.ListAsync(default)); Check(images.Calls == 0, "invalid URL downloaded");
        }
    }

    private static async Task ExactQqCdn()
    {
        var api = new Api { Urls = ["https://multimedia.nt.qq.com.cn:443/synthetic.png"] }; var images = new Images();
        using var client = Client(api, images);
        var list = await client.ListAsync(default);
        Check(list.Count == 1 && !JsonSerializer.Serialize(list).Contains("qq.com.cn", StringComparison.Ordinal), "CDN source escaped the opaque catalog");
        var asset = await client.ReadAsync(list[0].Id, default);
        Check(asset.Content.SequenceEqual(MemeLibraryChecks.Png) && images.Calls == 1 && !images.HadAuthorization,
            "exact QQ CDN image was rejected or received local credentials");
        images.Mode = "redirect";
        await RejectAsync(() => client.ReadAsync(list[0].Id, default));
        Check(images.Calls == 2, "CDN redirect was followed or retried");
    }

    private static async Task Catalog()
    {
        var api = new Api(); var images = new Images(); var time = new Clock();
        using var client = new QqEmojiClient("http://127.0.0.1:3000/", "test-token", 1, api, images, time);
        await RejectAsync(() => client.ReadAsync("missing", default));
        var id = (await client.ListAsync(default))[0].Id;
        api.Urls = ["https://p.qpic.cn/new.png"]; await client.ListAsync(default);
        await RejectAsync(() => client.ReadAsync(id, default));
        var current = (await client.ListAsync(default))[0].Id; time.Now = time.Now.AddMinutes(10);
        await RejectAsync(() => client.ReadAsync(current, default)); Check(images.Calls == 0, "stale catalog downloaded");
    }

    private static async Task AccountChange()
    {
        var api = new Api { Account = 2 }; var images = new Images(); using (var client = Client(api, images))
        { await RejectAsync(() => client.ListAsync(default)); Check(!api.Actions.Contains("fetch_custom_face"), "wrong account read"); }
        api = new Api(); images = new Images(); using (var client = Client(api, images))
        {
            var id = (await client.ListAsync(default))[0].Id;
            images.After = () => api.Account = 2;
            await RejectAsync(() => client.ReadAsync(id, default)); api.Account = 1;
            await RejectAsync(() => client.ReadAsync(id, default));
            Check(images.Calls == 1, "account change did not clear selection");
        }
    }

    private static async Task Downloads()
    {
        foreach (var mode in new[] { "redirect", "length", "oversize", "malformed", "encoding", "uri" })
        {
            var api = new Api(); var images = new Images { Mode = mode }; using var client = Client(api, images);
            var id = (await client.ListAsync(default))[0].Id;
            await RejectAsync(() => client.ReadAsync(id, default)); Check(images.Calls == 1, "download retried");
        }
    }

    private static async Task Cancellation()
    {
        var api = new Api(); var images = new Images(); using var client = Client(api, images);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try { await client.ListAsync(cts.Token); throw new Exception("cancel accepted"); } catch (OperationCanceledException) { }
        Check(api.Actions.Count == 0, "cancelled lookup sent HTTP");
        var id = (await client.ListAsync(default))[0].Id;
        images.Mode = "cancel"; using var next = new CancellationTokenSource(); var read = client.ReadAsync(id, next.Token);
        await images.Entered.Task; next.Cancel();
        try { await read; throw new Exception("download ignored cancellation"); } catch (OperationCanceledException) { }
    }

    private static async Task ApiShape()
    {
        foreach (var raw in new[] { "{\"status\":\"failed\",\"retcode\":1,\"data\":[]}", "{\"status\":\"ok\",\"retcode\":0,\"data\":{}}", "{\"status\":\"ok\",\"retcode\":0,\"data\":[7]}", JsonSerializer.Serialize(new { status = "ok", retcode = 0, data = Enumerable.Repeat("https://p.qpic.cn/x", 49) }) })
        {
            var api = new Api { Raw = raw }; using var client = Client(api, new Images());
            await RejectAsync(() => client.ListAsync(default));
        }
        using var empty = Client(new Api { Urls = [] }, new Images()); Check((await empty.ListAsync(default)).Count == 0, "empty favorites rejected");
    }

    private static QqEmojiClient Client(Api api, Images images) => new("http://127.0.0.1:3000/", "test-token", 1, api, images);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("invalid configuration accepted"); }
    private static async Task RejectAsync(Func<Task> action)
    { try { await action(); } catch (Exception error) when (error is ArgumentException or IOException) { return; } throw new Exception("unsafe input accepted"); }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Api : HttpMessageHandler
    {
        public long Account = 1;
        public string[] Urls = ["https://p.qpic.cn/test.png"];
        public string? Raw;
        public readonly List<string> Actions = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Check(request.Headers.Authorization?.ToString() == "Bearer test-token", "missing local authentication");
            var action = request.RequestUri!.AbsolutePath.Trim('/'); Actions.Add(action);
            Check(action is "get_login_info" or "fetch_custom_face", "client called mutation endpoint");
            if (action == "fetch_custom_face") Check((await request.Content!.ReadAsStringAsync(ct)).Contains("48"), "unbounded favorite count");
            return new(HttpStatusCode.OK) { Content = new StringContent(action == "get_login_info"
                ? JsonSerializer.Serialize(new { status = "ok", retcode = 0, data = new { user_id = Account } })
                : Raw ?? JsonSerializer.Serialize(new { status = "ok", retcode = 0, data = Urls }), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Images : HttpMessageHandler
    {
        public int Calls;
        public bool HadAuthorization;
        public string Mode = "ok";
        public Action? After;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; HadAuthorization |= request.Headers.Authorization is not null; Entered.TrySetResult();
            if (Mode == "cancel") await Task.Delay(Timeout.Infinite, ct);
            var bytes = Mode == "malformed" ? "not an image"u8.ToArray() : Mode == "oversize" ? new byte[OneBotMessageClient.MaximumImageBytes + 1] : MemeLibraryChecks.Png;
            var response = new HttpResponseMessage(Mode == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            if (Mode == "redirect") response.Headers.Location = new("https://evil.example/x");
            if (Mode == "length") response.Content.Headers.ContentLength = OneBotMessageClient.MaximumImageBytes + 1;
            if (Mode == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            if (Mode == "uri") response.RequestMessage = new(HttpMethod.Get, "https://evil.example/x");
            After?.Invoke(); return response;
        }
    }
}
