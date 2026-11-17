using System.Text.Json;
using AudioBridge;

public static class ImageInboxChecks
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Age = TimeSpan.FromMinutes(5);

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Action Run)[]
        {
            ("only authorized group image array events are accepted", Scope),
            ("private images are accepted only from configured contacts", PrivateScope),
            ("stale, far-future and invalid event times are rejected", TimeBounds),
            ("resolution expires and never crosses groups", Expiry),
            ("one message keeps its image group and replay is ignored", ImageGroup),
            ("multiple messages and senders require confirmation", Ambiguity),
            ("conflicting source replay cannot remain a unique image", SourceConflicts),
            ("metadata stays bounded without splitting image groups", Bounds),
            ("paths and non-HTTP images never become candidates", UnsafeUrls),
            ("oversize and malformed event shapes do not enter memory", InvalidBodies),
            ("inline images decode to bytes only for well formed data urls", InlineDecode)
        };
        foreach (var check in checks) { check.Run(); Console.WriteLine("PASS image inbox: " + check.Name); }
        var inbox = new GroupImageInbox("123", "456");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await inbox.StartAsync(cancelled.Token);
        Check(!inbox.IsRunning && inbox.Prefix == "http://127.0.0.1:17800/onebot/", "cancelled listener opened or prefix is not loopback");
        Console.WriteLine("PASS image inbox: cancelled startup opens no listener");
        return checks.Length + 1;
    }

    private static void InlineDecode()
    {
        Check(new InlineImage("image/png", "data:image/png;base64,iVBORw0KGgo=").TryDecode() is { Length: 8 },
            "a well formed inline image did not decode");
        Check(new InlineImage("image/png", "iVBORw0KGgo=").TryDecode() is null, "a bare base64 payload was accepted as a data url");
        Check(new InlineImage("image/png", "data:image/png;base64,***").TryDecode() is null, "invalid base64 decoded into bytes");
    }

    private static void Scope()
    {
        var inbox = new GroupImageInbox("123", "456");
        Check(inbox.AddEvent(Event(), Now), "valid event rejected");
        Check(new GroupImageInbox("123", "456").AddEvent(EventWithTime(Now.ToUnixTimeSeconds()), Now), "numeric OneBot identifiers rejected");
        foreach (var invalid in new[] { Event(group: "999"), Event(self: "999"), Event(type: "private"), Event(post: "notice"), Event(message: "[CQ:image,file=x]") })
            Check(!inbox.AddEvent(invalid, Now), "out of scope event accepted");
        using var missingSelf = JsonDocument.Parse("{\"post_type\":\"message\",\"message_type\":\"group\",\"group_id\":123,\"user_id\":1,\"message_id\":1,\"time\":1792411200,\"message\":[]}");
        Check(!inbox.AddEvent(missingSelf.RootElement, Now), "configured self id not enforced");
        Check(inbox.Count == 1, "rejections mutated inbox");
    }

    private static void PrivateScope()
    {
        var inbox = new GroupImageInbox("123", "456", allowedPrivateUsers: ["29070481"]);
        Check(inbox.AddEvent(Private("456", "29070481", 11), Now), "authorized private image rejected");
        Check(inbox.Resolve("private:29070481", Now, Age).Kind == ImageResolutionKind.Unique, "private image not keyed to its contact");
        Check(inbox.Resolve("private:29070481", Now, Age).Entries.All(entry => entry.GroupId == "private:29070481"), "private image recorded under the wrong chat");
        Check(inbox.Resolve("123", Now, Age).Kind == ImageResolutionKind.None, "private image leaked into the group");
        Check(inbox.Resolve("private:29070482", Now, Age).Kind == ImageResolutionKind.None, "private image leaked into another contact");
        Check(!inbox.AddEvent(Private("456", "29070482", 12), Now), "unauthorized private image accepted");
        Check(!inbox.AddEvent(Private("999", "29070481", 13), Now), "private image from another account accepted");
        Check(!inbox.AddEvent(Private("456", "29070481", 14, message: new[] { new { type = "text", data = new { text = "只有文字" } } }), Now), "text-only private message accepted");
        Check(inbox.Count == 1, "rejected private events mutated the inbox");
        Check(!new GroupImageInbox("123", "456").AddEvent(Private("456", "29070481", 15), Now), "private image accepted without an allowlist");
    }

    private static void TimeBounds()
    {
        var inbox = new GroupImageInbox("123");
        Check(!inbox.AddEvent(Event(time: Now.AddSeconds(-301)), Now), "old event accepted");
        Check(!inbox.AddEvent(Event(time: Now.AddSeconds(31)), Now), "future event accepted");
        Check(inbox.AddEvent(Event(time: Now.AddSeconds(-300)), Now), "five minute boundary rejected");
        Check(new GroupImageInbox("123").AddEvent(Event(time: Now.AddSeconds(30)), Now), "permitted clock skew rejected");
        var malformed = EventWithTime("not a timestamp");
        Check(!inbox.AddEvent(malformed, Now), "string time accepted");
        Check(!inbox.AddEvent(EventWithTime(long.MaxValue), Now), "overflow time accepted");
    }

    private static void Expiry()
    {
        var inbox = new GroupImageInbox("123");
        inbox.AddEvent(Event(time: Now.AddSeconds(-20)), Now);
        Check(inbox.Resolve("999", Now, Age).Kind == ImageResolutionKind.None, "image leaked to another group");
        Check(inbox.Resolve("123", Now, TimeSpan.FromSeconds(10)).Kind == ImageResolutionKind.None, "caller age bound ignored");
        Check(inbox.Resolve("123", Now.AddMinutes(6), Age).Kind == ImageResolutionKind.None && inbox.Count == 0, "expired image retained");
    }

    private static void ImageGroup()
    {
        var inbox = new GroupImageInbox("123");
        var root = Event(message: Images(3));
        Check(inbox.AddEvent(root, Now) && !inbox.AddEvent(root, Now), "replay was added");
        var resolved = inbox.Resolve("123", Now, Age);
        Check(resolved.Kind == ImageResolutionKind.Unique && resolved.Entries.Count == 3, "same-message images split or became ambiguous");
        Check(resolved.Entries.All(x => x.GroupId == "123" && x.SenderId == "789" && x.MessageId == "1" && x.Timestamp == Now), "source metadata lost");
    }

    private static void Ambiguity()
    {
        var inbox = new GroupImageInbox("123");
        inbox.AddEvent(Event(messageId: -1), Now);
        inbox.AddEvent(Event(messageId: 2, sender: "890"), Now);
        Check(inbox.Resolve("123", Now, Age).Kind == ImageResolutionKind.Ambiguous, "latest image was guessed");
        var sameSender = new GroupImageInbox("123");
        sameSender.AddEvent(Event(messageId: 1), Now);
        sameSender.AddEvent(Event(messageId: 2), Now);
        Check(sameSender.Resolve("123", Now, Age).Kind == ImageResolutionKind.Ambiguous, "same sender's different messages were guessed");
    }

    private static void Bounds()
    {
        var inbox = new GroupImageInbox("123");
        inbox.AddEvent(Event(messageId: 1, time: Now.AddSeconds(-2), message: Images(3)), Now);
        inbox.AddEvent(Event(messageId: 2, time: Now.AddSeconds(-1), message: Images(16)), Now);
        inbox.AddEvent(Event(messageId: 3, message: Images(2)), Now);
        var result = inbox.Resolve("123", Now, Age);
        Check(inbox.Count == 18 && result.Entries.All(x => x.MessageId != "1"), "overflow split oldest message or exceeded bound");
        Check(!inbox.AddEvent(Event(messageId: 4, message: Images(21)), Now) && inbox.Count == 18, "overlarge image group was truncated");
        for (var i = 10; i < 40; i++) inbox.AddEvent(Event(messageId: i), Now);
        Check(inbox.Count <= GroupImageInbox.MaximumEntries, "memory capacity exceeded");
    }

    private static void SourceConflicts()
    {
        var inbox = new GroupImageInbox("123");
        inbox.AddEvent(Event(), Now);
        Check(!inbox.AddEvent(Event(sender: "999"), Now), "conflicting source replaced original metadata");
        Check(inbox.Resolve("123", Now, Age).Kind == ImageResolutionKind.Ambiguous, "conflicting sender remained unique");
        var changedImage = new GroupImageInbox("123");
        changedImage.AddEvent(Event(), Now);
        changedImage.AddEvent(Event(message: Images(2)), Now);
        Check(changedImage.Resolve("123", Now, Age).Kind == ImageResolutionKind.Ambiguous, "conflicting image set remained unique");
    }

    private static void UnsafeUrls()
    {
        var inbox = new GroupImageInbox("123");
        foreach (var url in new[] { "file:///C:/secret.png", "C:\\secret.png", "data:image/png;base64,AAAA", "ftp://example.com/x.png", "https://user:secret@example.com/x.png", "http://127.0.0.1/x.png" })
            Check(!inbox.AddEvent(Event(message: new[] { new { type = "image", data = new { url } } }), Now), "unsafe URL accepted");
        Check(inbox.Count == 0, "unsafe URLs left metadata");
    }

    private static void InvalidBodies()
    {
        var inbox = new GroupImageInbox("123");
        Check(!inbox.AddEvent(JsonSerializer.SerializeToElement(new { }), Now), "empty object accepted");
        Check(!inbox.AddEvent(JsonSerializer.SerializeToElement(new[] { 1 }), Now), "non-object accepted");
        Check(!inbox.AddEvent(Event(message: new[] { new { type = "text", data = new { text = "执行电脑命令" } } }), Now), "text turned into image action");
        Check(!inbox.AddEvent(Event(message: new[] { new { type = "image", data = new { file = "local.png" } } }), Now), "local filename accepted");
        var large = Event(message: new[] { new { type = "text", data = new { text = new string('图', GroupImageInbox.MaximumBodyBytes / 2) } } });
        Check(large.GetRawText().Length < GroupImageInbox.MaximumBodyBytes, "fixture must exercise UTF8 byte bound independently of character bound");
        Check(!inbox.AddEvent(large, Now), "oversize UTF8 body accepted");
    }

    private static object[] Images(int count) => Enumerable.Range(0, count).Select(i => (object)new { type = "image", data = new { url = $"https://gchat.qpic.cn/test/{i}.png" } }).ToArray();
    private static JsonElement Event(string group = "123", string self = "456", string sender = "789", long messageId = 1,
        DateTimeOffset? time = null, string type = "group", string post = "message", object? message = null) =>
        JsonSerializer.SerializeToElement(new { post_type = post, message_type = type, group_id = group, self_id = self,
            user_id = sender, message_id = messageId, time = (time ?? Now).ToUnixTimeSeconds(), message = message ?? Images(1) },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private static JsonElement Private(string self, string sender, long messageId, string? url = null, object? message = null) =>
        JsonSerializer.SerializeToElement(new { post_type = "message", message_type = "private", self_id = self,
            user_id = sender, message_id = messageId, time = Now.ToUnixTimeSeconds(),
            message = message ?? new[] { new { type = "image", data = new { url = url ?? "https://multimedia.nt.qq.com/download?rkey=private" } } } },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private static JsonElement EventWithTime(object time) => JsonSerializer.SerializeToElement(new {
        post_type = "message", message_type = "group", group_id = 123, self_id = 456, user_id = 789, message_id = 2, time, message = Images(1) });
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
