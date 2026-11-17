using System.Net;
using System.Text;
using System.Text.Json;
using AudioBridge;

public static class QzoneChecks
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 30, 0, TimeSpan.Zero); // 17:30 Shanghai
    private const string Cookies = "uin=o1667533356; skey=@6WhDkkOdv; p_skey=abc; pt4_token=xyz";

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("a stable daily target sits inside the publishing window", Schedule),
            ("one attempt per local day, never outside the window", Window),
            ("the attempt marker survives restarts and rejects damage", Store),
            ("JSONP success and rejection are told apart strictly", Response),
            ("publishing posts the exact form, cookie and g_tk once", Publish),
            ("missing credentials never reach the network", Credentials),
            ("feeds and comments are read from the rendered space page", Comments)
        };
        foreach (var check in checks)
        {
            await check.Run().WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine("PASS qzone: " + check.Name);
        }
        return checks.Length;
    }

    private static Task Schedule()
    {
        var target = QzoneSchedule.Target(Now);
        var local = CompanionStore.ShanghaiTime(target);
        Check(QzoneSchedule.Target(Now) == target, "target time changed inside one day");
        Check(local.Hour is >= QzoneSchedule.FromHour and < QzoneSchedule.ToHour, "target fell outside the window");
        var others = Enumerable.Range(0, 30).Select(day => CompanionStore.ShanghaiTime(QzoneSchedule.Target(Now.AddDays(day))).Minute).Distinct().Count();
        Check(others > 1, "every day was scheduled at the same minute");
        return Task.CompletedTask;
    }

    private static Task Window()
    {
        var target = QzoneSchedule.Target(Now);
        Check(!QzoneSchedule.ShouldPublish(false, null, target), "disabled switch still published");
        Check(!QzoneSchedule.ShouldPublish(true, null, target.AddMinutes(-1)), "published before the daily target");
        Check(QzoneSchedule.ShouldPublish(true, null, target), "daily target did not open");
        Check(!QzoneSchedule.ShouldPublish(true, target, target.AddHours(1)), "same day published twice");
        Check(!QzoneSchedule.ShouldPublish(true, null, new DateTimeOffset(2026, 10, 26, 23, 30, 0, TimeSpan.FromHours(8))),
            "published outside the evening window");
        Check(!QzoneSchedule.ShouldPublish(true, null, new DateTimeOffset(2026, 10, 26, 8, 59, 0, TimeSpan.FromHours(8))),
            "published before nine in the morning");
        return Task.CompletedTask;
    }

    private static Task Store()
    {
        using var folder = new Temp();
        var store = folder.Open(Now);
        Check(store.Load().LastAttemptAt is null && store.Load().HandledComments.Count == 0 && !store.Load().Seeded,
            "fresh store invented state");
        store.MarkAttempt(Now);
        store.MarkHandled(["feed:1", "feed:2"]);
        store.MarkSeeded();
        var reloaded = folder.Open(Now).Load();
        Check(reloaded.LastAttemptAt == Now && reloaded.Seeded && reloaded.HandledComments.Count == 2, "space state did not survive a restart");
        File.WriteAllText(folder.StatePath, "not-json");
        try { store.Load(); throw new Exception("damaged state was accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(folder.StatePath) == "not-json", "damaged state was reset");
        var tomorrow = folder.Open(Now.AddDays(1));
        try { tomorrow.MarkAttempt(Now.AddDays(1)); throw new Exception("damaged state was silently overwritten"); }
        catch (InvalidDataException) { }
        File.Delete(folder.StatePath);
        tomorrow.MarkAttempt(Now.AddDays(1));
        Check(tomorrow.Load().LastAttemptAt == Now.AddDays(1), "a fresh state could not be written after damage was removed");
        return Task.CompletedTask;
    }

    private static Task Response()
    {
        Check(QzonePublisher.Accepted("<script>frameElement.callback({\"code\":0,\"subcode\":0});</script>"), "success was not recognised");
        Check(QzonePublisher.Accepted("_Callback({\"err\":{\"code\":0}})"), "err.code success was not recognised");
        Check(!QzonePublisher.Accepted("_Callback({\"code\":-3000})"), "rejected response was treated as success");
        Check(!QzonePublisher.Accepted("_Callback({\"err\":{\"code\":100,\"message\":\"x\"}})"), "error code passed");
        foreach (var body in new[] { "", "   ", "no-jsonp", "()", "_(not json)" })
            Check(!QzonePublisher.Accepted(body), "garbage was accepted as a publish result");
        return Task.CompletedTask;
    }

    private static async Task Publish()
    {
        var calls = 0;
        var handler = new Handler(async (request, _) =>
        {
            calls++;
            var url = request.RequestUri!;
            Check(url.Host == "user.qzone.qq.com" && url.AbsolutePath.EndsWith("/emotion_cgi_publish_v6", StringComparison.Ordinal),
                "unexpected publish endpoint");
            Check(url.Query.Contains("g_tk=", StringComparison.Ordinal), "publish request missed g_tk");
            Check(request.Headers.TryGetValues("Cookie", out var cookies) && cookies.Single().Contains("skey=", StringComparison.Ordinal),
                "publish request missed the session cookie");
            var form = await request.Content!.ReadAsStringAsync();
            Check(form.Contains("hostuin=1667533356", StringComparison.Ordinal) &&
                  form.Contains("secret=0", StringComparison.Ordinal) &&
                  form.Contains("ugc_right=1", StringComparison.Ordinal), "publish form changed");
            return Reply("<script>frameElement.callback({\"code\":0,\"err\":{\"code\":0}});</script>");
        });
        using var publisher = new QzonePublisher(_ => Task.FromResult(Cookies), null, handler);
        await publisher.PublishAsync("今天也要记得好好吃饭呀。", default);
        Check(calls == 1, "publish request was not sent once");
        using var rejecting = new QzonePublisher(_ => Task.FromResult(Cookies), null,
            new Handler((_, _) => Task.FromResult(Reply("_Callback({\"code\":-3000})"))));
        try { await rejecting.PublishAsync("被拒绝", default); throw new Exception("rejected publish looked successful"); }
        catch (InvalidDataException) { }
        foreach (var text in new[] { "", "   ", new string('x', QzonePublisher.MaximumCharacters + 1), "带\0控制字符" })
        {
            try { await publisher.PublishAsync(text, default); throw new Exception("invalid post text was accepted"); }
            catch (InvalidDataException) { }
        }
        Check(calls == 1, "invalid text reached the network");
    }

    private static async Task Credentials()
    {
        var calls = 0;
        using var publisher = new QzonePublisher(_ => Task.FromResult("uin=o1; pt4_token=only"),
            null, new Handler((_, _) => { calls++; return Task.FromResult(Reply("unused")); }));
        try { await publisher.PublishAsync("没有 skey", default); throw new Exception("incomplete credentials were used"); }
        catch (InvalidDataException) { }
        Check(calls == 0, "incomplete credentials still reached the network");
        using var failing = new QzonePublisher(_ => throw new OneBotMessageException("no cookies", false));
        try { await failing.PublishAsync("取不到凭证", default); throw new Exception("cookie failure was swallowed"); }
        catch (InvalidDataException) { }
    }

    private static Task Comments()
    {
        const string html = "<li class=\"f-single f-s-s\" id=\"fct_1_311_2_1_1\"><i class=\"none\" name=\"feed_data\" data-tid=\"2c8a6463c637de6abe120000\" data-uin=\"1667533356\" data-topicid=\"1667533356_2c8a6463c637de6abe120000__1\" data-abstime=\"1792985416\"></i>" +
            "<p class=\"txt-box-title ellipsis-one\"><a class=\"nickname name\" href=\"x\">大肥鱼</a>&nbsp; <span class=\" state\" > ： </span> 今天也要记得好好吃饭&amp;睡觉呀。 </p>" +
            "<ul><li class=\"comments-item bor3\" data-type=\"commentroot\" data-tid=\"1\" data-uin=\"29070481\" data-nick=\"示熠\" data-who=\"1\">" +
            "<a class=\"nickname name\" href=\"y\">示熠</a>&nbsp; : 小鱼你会发空间啦 <div class=\"comments-op\"> <span class=\" ui-mr10 state\" > 01:15 </span></div></li>" +
            "<li class=\"comments-item bor3\" data-type=\"reply\" data-tid=\"2\" data-uin=\"2515923747\" data-nick=\"小糯米团子\">只回复别人的回复</li></ul></li>";
        var feeds = QzoneFeedReader.Parse(html);
        var feed = feeds.Single();
        Check(feed.Tid == "2c8a6463c637de6abe120000" && feed.OwnerUin == 1667533356 && feed.PublishedAt == 1792985416,
            "feed identity was not read from the page");
        Check(feed.Content == "今天也要记得好好吃饭&睡觉呀。", "feed text was not decoded: " + feed.Content);
        var comment = feed.Comments.Single();
        Check(comment.Id == "2c8a6463c637de6abe120000:1" && comment.AuthorUin == 29070481 && comment.Author == "示熠" &&
              comment.Text == "小鱼你会发空间啦" && comment.DisplayTime == "01:15", "comment fields changed: " + comment);
        Check(QzoneFeedReader.Parse("").Count == 0 && QzoneFeedReader.Parse("<html>登录</html>").Count == 0, "garbage produced feeds");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }

    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private sealed class Temp : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "audio-bridge-qzone-check-" + Guid.NewGuid().ToString("N"));
        public string StatePath => System.IO.Path.Combine(Root, "Results", "local", "qzone-state.json");
        public Temp() => Directory.CreateDirectory(Root);
        public QzoneStore Open(DateTimeOffset now) => new(Root, () => now);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request, ct);
    }
}
