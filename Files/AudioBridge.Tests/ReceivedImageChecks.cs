using System.Net;
using System.Text;
using System.Text.Json;
using AudioBridge;

public static class ReceivedImageChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("only policy-authorized group and private image metadata is listed", Scope),
            ("parsed and raw identity, freshness and message shape must agree", Metadata),
            ("untrusted URLs and local files never become selections", Urls),
            ("current QQ multimedia CDN is accepted only as an exact HTTPS host", QqCdn),
            ("duplicates stay stable and conflicting replays revoke selections", Replays),
            ("candidate count and lifetime are bounded and clear revokes selections", Bounds),
            ("selected image download verifies account and withholds credentials", Downloads),
            ("invalid assets and account changes cannot return received bytes", Failure),
            ("cancelled or cleared selections cannot supply late bytes", Cancellation)
        };
        foreach (var check in checks) { await check.Run(); Console.WriteLine("PASS received images: " + check.Name); }
        return checks.Length;
    }

    private static Task Scope()
    {
        var clock = new Clock(); var inbox = new ReceivedImageInbox(1, clock);
        using var group = Event(clock, groupId: 10);
        using var contact = Event(clock, isPrivate: true, messageId: 2);
        Check(Admit(inbox, group, clock) && Admit(inbox, contact, clock), "authorized images were rejected");
        Check(inbox.List().Count == 2 && inbox.List().Any(item => item.IsPrivate && item.SenderId == 20 && item.GroupId == 0) &&
            inbox.List().Any(item => !item.IsPrivate && item.GroupId == 10), "source labels were lost");
        var json = JsonSerializer.Serialize(inbox.List());
        Check(!json.Contains("qpic") && !json.Contains("private text") && !json.Contains("url", StringComparison.OrdinalIgnoreCase), "metadata exposed URL or text");
        using var wrongGroup = Event(clock, groupId: 99, messageId: 3);
        using var stranger = Event(clock, isPrivate: true, sender: 99, messageId: 4);
        using var self = Event(clock, sender: 1, messageId: 5);
        Check(!Admit(inbox, wrongGroup, clock) && !Admit(inbox, stranger, clock) && !Admit(inbox, self, clock) && inbox.List().Count == 2,
            "policy allowlist bypassed");
        return Task.CompletedTask;
    }

    private static Task Metadata()
    {
        var clock = new Clock(); var inbox = new ReceivedImageInbox(1, clock); using var item = Event(clock);
        var parsed = Parse(item, clock);
        foreach (var changed in new[] { parsed with { SenderId = 30 }, parsed with { GroupId = 30 }, parsed with { MessageId = 30 },
                     parsed with { At = parsed.At.AddSeconds(1) }, parsed with { IsPrivate = true }, parsed with { ImageSeen = false } })
            Check(!inbox.Observe(item.RootElement, changed), "inconsistent parsed metadata admitted");
        using var wrongSelf = Event(clock, self: 2);
        Check(!inbox.Observe(wrongSelf.RootElement, parsed), "wrong self admitted");
        using var selfSent = Event(clock, subtype: "self_sent");
        Check(!inbox.Observe(selfSent.RootElement, parsed), "self event admitted");
        using var old = Event(clock, secondsOffset: -121);
        Check(!inbox.Observe(old.RootElement, parsed with { At = clock.Now.AddSeconds(-121) }), "old event admitted");
        using var future = Event(clock, secondsOffset: 31);
        Check(!inbox.Observe(future.RootElement, parsed with { At = clock.Now.AddSeconds(31) }), "future event admitted");
        using var text = JsonDocument.Parse("{\"message\":[]}");
        Check(!inbox.Observe(text.RootElement, parsed), "invalid event admitted");
        Check(inbox.List().Count == 0, "invalid input left selections");
        return Task.CompletedTask;
    }

    private static Task Urls()
    {
        var clock = new Clock(); var inbox = new ReceivedImageInbox(1, clock);
        foreach (var url in new[] { "http://p.qpic.cn/a", "https://p.qpic.cn.evil.test/a", "https://evilqpic.cn/a", "https://127.0.0.1/a",
                     "file:///C:/secret.png", "base64://abc", "https://u@p.qpic.cn/a", "https://p.qpic.cn:444/a", "https://p.qpic.cn/a#x",
                     "http://multimedia.nt.qq.com.cn/a", "https://multimedia.nt.qq.com.cn:444/a", "https://sub.multimedia.nt.qq.com.cn/a",
                     "https://multimedia.nt.qq.com.cn.evil.test/a", "https://evilqq.com.cn/a", "https://u@multimedia.nt.qq.com.cn/a" })
        { using var item = Event(clock, url: url); Check(!inbox.Observe(item.RootElement, Parse(item, clock)), "unsafe URL admitted"); }
        using var file = Event(clock, url: null);
        Check(!inbox.Observe(file.RootElement, Parse(file, clock)) && inbox.List().Count == 0, "file fallback admitted");
        return Task.CompletedTask;
    }

    private static async Task QqCdn()
    {
        var clock = new Clock(); var inbox = new ReceivedImageInbox(1, clock);
        using var item = Event(clock, url: "https://multimedia.nt.qq.com.cn:443/synthetic.png");
        Check(Admit(inbox, item, clock), "exact QQ multimedia CDN was rejected");
        var api = new Api(); var images = new Images(); using var client = Client(api, images);
        var asset = await client.ReadReceivedAsync(inbox, inbox.List().Single().Id, default);
        Check(asset.Content.SequenceEqual(MemeLibraryChecks.Png) && images.Calls == 1 && !images.HadAuthorization,
            "accepted QQ CDN download changed bytes or exposed credentials");
        images.Mode = "redirect";
        await Reject(() => client.ReadReceivedAsync(inbox, inbox.List().Single().Id, default));
        Check(images.Calls == 2, "QQ CDN redirect was retried");
    }

    private static Task Replays()
    {
        var clock = new Clock(); var inbox = new ReceivedImageInbox(1, clock); using var first = Event(clock);
        Check(Admit(inbox, first, clock), "first event rejected");
        var id = inbox.List()[0].Id;
        Check(!Admit(inbox, first, clock) && inbox.List().Single().Id == id, "replay replaced selection");
        using var changed = Event(clock, url: "https://p.qpic.cn/changed.png");
        Check(!Admit(inbox, changed, clock) && inbox.List().Count == 0, "conflicting replay remained selectable");
        Check(!Admit(inbox, first, clock) && inbox.List().Count == 0, "conflicting selection reappeared");
        return Task.CompletedTask;
    }

    private static Task Bounds()
    {
        var clock = new Clock(); var inbox = new ReceivedImageInbox(1, clock);
        for (var i = 0; i < ReceivedImageInbox.MaximumItems + 12; i++) { using var item = Event(clock, messageId: i + 1); Check(Admit(inbox, item, clock), "bounded catalog stopped accepting latest events"); }
        Check(inbox.List().Count == ReceivedImageInbox.MaximumItems && inbox.List().All(item => item.MessageId > 12), "old candidates were not evicted");
        clock.Now = clock.Now.AddHours(25);
        Check(inbox.List().Count == 0, "expired candidate remained");
        using var next = Event(clock, messageId: 100); Check(Admit(inbox, next, clock), "new candidate rejected after expiry");
        inbox.Clear(); Check(inbox.List().Count == 0, "clear left catalog");
        return Task.CompletedTask;
    }

    private static async Task Downloads()
    {
        var clock = new Clock(); var inbox = new ReceivedImageInbox(1, clock); using var item = Event(clock); Admit(inbox, item, clock);
        var api = new Api(); var images = new Images(); using var client = Client(api, images);
        var asset = await client.ReadReceivedAsync(inbox, inbox.List().Single().Id, default);
        Check(asset.Content.SequenceEqual(MemeLibraryChecks.Png) && asset.MediaType == "image/png", "image changed");
        Check(images.Calls == 1 && api.Calls == 2 && !images.HadAuthorization, "account not verified or credentials leaked");
        await Reject(() => client.ReadReceivedAsync(inbox, "https://p.qpic.cn/arbitrary", default));
        Check(images.Calls == 1, "arbitrary browser ID downloaded");
        using var mismatch = new QqEmojiClient("http://127.0.0.1:3000/", "test-token", 2, new Api(), new Images());
        await Reject(() => mismatch.ReadReceivedAsync(inbox, inbox.List()[0].Id, default));
    }

    private static async Task Failure()
    {
        foreach (var mode in new[] { "redirect", "oversized", "malformed", "encoding", "uri", "account" })
        {
            var clock = new Clock(); var inbox = new ReceivedImageInbox(1, clock); using var item = Event(clock); Admit(inbox, item, clock);
            var api = new Api(); var images = new Images { Mode = mode }; if (mode == "account") images.After = () => api.Account = 2;
            using var client = Client(api, images);
            await Reject(() => client.ReadReceivedAsync(inbox, inbox.List()[0].Id, default));
            Check(images.Calls == 1, "received image failure retried");
        }
        var clock2 = new Clock(); var inbox2 = new ReceivedImageInbox(1, clock2); using var other = Event(clock2); Admit(inbox2, other, clock2);
        var images2 = new Images(); using var wrong = Client(new Api { Account = 2 }, images2);
        await Reject(() => wrong.ReadReceivedAsync(inbox2, inbox2.List()[0].Id, default));
        Check(images2.Calls == 0, "wrong login downloaded image");
    }

    private static async Task Cancellation()
    {
        var clock = new Clock(); var inbox = new ReceivedImageInbox(1, clock); using var item = Event(clock); Admit(inbox, item, clock);
        var api = new Api(); var images = new Images { Mode = "cancel" }; using var client = Client(api, images);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await client.ReadReceivedAsync(inbox, inbox.List()[0].Id, cancelled.Token); throw new Exception("pre-cancel ignored"); }
        catch (OperationCanceledException) { }
        Check(api.Calls == 0, "pre-cancel sent HTTP");
        using var current = new CancellationTokenSource(); var reading = client.ReadReceivedAsync(inbox, inbox.List()[0].Id, current.Token);
        await images.Entered.Task; current.Cancel();
        try { await reading; throw new Exception("download ignored cancel"); } catch (OperationCanceledException) { }
        images.Mode = "ok"; images.After = inbox.Clear;
        await Reject(() => client.ReadReceivedAsync(inbox, inbox.List()[0].Id, default));
        Check(inbox.List().Count == 0, "late selection survived clear");
    }

    private static QqEmojiClient Client(Api api, Images images) => new("http://127.0.0.1:3000/", "test-token", 1, api, images);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch (Exception error) when (error is IOException or ArgumentException) { return; } throw new Exception("unsafe selection returned bytes"); }
    private static GroupChatPolicy Policy(Clock clock) => new(new() { SelfId = 1, AllowedGroupIds = [10], AllowedPrivateUserIds = [20] }, () => clock.Now);
    private static ParsedGroupMessage Parse(JsonDocument item, Clock clock)
    { Check(Policy(clock).TryParse(item.RootElement, out var parsed, out _), "fixture policy failed"); return parsed!; }
    private static bool Admit(ReceivedImageInbox inbox, JsonDocument item, Clock clock) =>
        Policy(clock).TryParse(item.RootElement, out var parsed, out _) && inbox.Observe(item.RootElement, parsed!);
    private static JsonDocument Event(Clock clock, long self = 1, long sender = 20, long groupId = 10, long messageId = 1,
        bool isPrivate = false, int secondsOffset = 0, string? url = "https://p.qpic.cn/received.png", string subtype = "normal") => JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            post_type = "message", message_type = isPrivate ? "private" : "group", sub_type = subtype,
            self_id = self, user_id = sender, group_id = groupId, message_id = messageId,
            sender = new { user_id = sender }, time = clock.Now.AddSeconds(secondsOffset).ToUnixTimeSeconds(),
            message = new[] { new { type = "image", data = new { url, file = "C:\\private.png" } } }
        }));
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Api : HttpMessageHandler
    {
        public long Account = 1; public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            Check(request.RequestUri!.AbsolutePath == "/get_login_info" && request.Headers.Authorization?.ToString() == "Bearer test-token", "unexpected API action");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { status = "ok", retcode = 0, data = new { user_id = Account } }), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Images : HttpMessageHandler
    {
        public string Mode = "ok"; public int Calls; public bool HadAuthorization; public Action? After;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; HadAuthorization |= request.Headers.Authorization is not null; Entered.TrySetResult();
            if (Mode == "cancel") await Task.Delay(Timeout.Infinite, ct);
            var response = new HttpResponseMessage(Mode == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK)
            { Content = new ByteArrayContent(Mode == "malformed" ? "not an image"u8.ToArray() : MemeLibraryChecks.Png) };
            if (Mode == "redirect") response.Headers.Location = new("https://evil.invalid/a");
            if (Mode == "oversized") response.Content.Headers.ContentLength = OneBotMessageClient.MaximumImageBytes + 1;
            if (Mode == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            if (Mode == "uri") response.RequestMessage = new(HttpMethod.Get, "https://evil.invalid/a");
            After?.Invoke(); return response;
        }
    }
}
