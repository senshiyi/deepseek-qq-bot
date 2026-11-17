using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using AudioBridge;

public static class ImageQuestionChecks
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] TinyPng = Png(1, 1);

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("ordinary questions never select or download group images", Ordinary),
            ("missing, expired and other-group images require a short clarification", ScopeAndAge),
            ("ambiguous messages and more than two same-message images never download", Ambiguity),
            ("confirmation cursor accepts only new message groups and preserves source conflicts", ConfirmationCursor),
            ("one or two unique tiny PNGs become memory-only inline images", Unique),
            ("unsafe URL schemes, hosts, ports and credentials never reach transport", UnsafeUrls),
            ("redirects are refused without following their destination", Redirect),
            ("declared and streaming image bodies remain bounded", ByteBounds),
            ("PNG signatures, checksums, truncation and dimensions are checked", InvalidImages),
            ("phone screenshots inside the size limits are accepted", ScreenshotSize),
            ("animated stickers sent as GIF or WebP are accepted", StickerFormats),
            ("JPEG frame dimensions and malformed segments are checked", JpegDimensions),
            ("network errors cannot leak signed URLs into clarification", NetworkFailure),
            ("an uncooperative download cannot hold a voice question past its deadline", Deadline),
            ("caller cancellation propagates and prevents late image attachment", Cancellation),
            ("chat context attaches the newest image once and keys it per chat", ChatAttach),
            ("the real QQ image host is accepted and a rejected host is reported without downloading", RealHosts),
            ("saving a sticker resolves the newest authorized image without the seen cursor", NewestForSaving)
        };
        foreach (var check in checks)
        {
            await check.Run().WaitAsync(TimeSpan.FromSeconds(2));
            Console.WriteLine("PASS image question: " + check.Name);
        }
        return checks.Length;
    }

    private static async Task Ordinary()
    {
        var inbox = Inbox("https://gchat.qpic.cn/tiny.png");
        var handler = Handler((_, _) => Task.FromResult(PngResponse()));
        using var context = new ImageQuestionContext(inbox, "123", handler);
        var result = await context.ResolveAsync("今天几点了", Now, default);
        Check(result.Clarification is null && result.Images.Count == 0 && handler.Calls == 0, "ordinary question attached an image");
    }

    private static async Task ScopeAndAge()
    {
        foreach (var scenario in new[] { "none", "expired", "other-group" })
        {
            var inbox = scenario == "none" ? new GroupImageInbox("123") : Inbox("https://gchat.qpic.cn/tiny.png",
                time: scenario == "expired" ? Now.AddSeconds(-181) : Now);
            var handler = Handler((_, _) => Task.FromResult(PngResponse()));
            using var context = new ImageQuestionContext(inbox, scenario == "other-group" ? "999" : "123", handler);
            var result = await context.ResolveAsync("刚发的这张图片是什么", Now, default);
            Clarification(result);
            Check(handler.Calls == 0, "out-of-scope image reached downloader");
        }
    }

    private static async Task Ambiguity()
    {
        var ambiguous = Inbox("https://gchat.qpic.cn/first.png");
        ambiguous.AddEvent(Event(["https://gchat.qpic.cn/second.png"], 2, sender: "999"), Now);
        var tooMany = Inbox("https://gchat.qpic.cn/1.png", "https://gchat.qpic.cn/2.png", "https://gchat.qpic.cn/3.png");
        foreach (var inbox in new[] { ambiguous, tooMany })
        {
            var handler = Handler((_, _) => Task.FromResult(PngResponse()));
            using var context = new ImageQuestionContext(inbox, "123", handler);
            var result = await context.ResolveAsync("这件装备怎么配", Now, default);
            Clarification(result);
            Check(result.Clarification!.Contains("哪一张") && handler.Calls == 0, "ambiguous image was guessed or downloaded");
        }
    }

    private static async Task Unique()
    {
        foreach (var urls in new[] { new[] { "https://multimedia.nt.qq.com/download?rkey=private" },
            new[] { "https://qpic.cn/1.png", "https://a.b.qpic.cn/2.png" } })
        {
            var handler = Handler((request, _) =>
            {
                Check(request.Method == HttpMethod.Get && request.Headers.Authorization is null, "download inherited API credentials");
                return Task.FromResult(PngResponse());
            });
            using var context = new ImageQuestionContext(Inbox(urls), "123", handler);
            var result = await context.ResolveAsync("帮我看图里的装备", Now, default);
            Check(result.Clarification is null && result.Images.Count == urls.Length && handler.Calls == urls.Length, "unique images missing");
            Check(result.Images.All(image => image.MimeType == "image/png" &&
                image.DataUrl == "data:image/png;base64," + Convert.ToBase64String(TinyPng)), "PNG bytes or MIME changed");
        }
    }

    private static async Task ConfirmationCursor()
    {
        foreach (var conflict in new[] { false, true })
        {
            var inbox = Inbox("https://gchat.qpic.cn/old1.png");
            inbox.AddEvent(Event(["https://gchat.qpic.cn/old2.png"], 2), Now);
            var handler = Handler((request, _) =>
            {
                Check(request.RequestUri!.AbsolutePath == "/resent.png", "confirmation fell back to an old or ambiguous image");
                return Task.FromResult(PngResponse());
            });
            using var context = new ImageQuestionContext(inbox, "123", handler);
            var asked = Now.AddMilliseconds(500);
            Clarification(await context.ResolveAsync("看图", asked, default));
            Clarification(await context.ResolveAsync("这张呢", asked.AddMilliseconds(100), default));
            Check(handler.Calls == 0, "waiting for confirmation reused an old image");
            // A new ID in the same second is a re-send; old IDs remain excluded.
            inbox.AddEvent(Event(["https://gchat.qpic.cn/resent.png"], 3), asked.AddMilliseconds(200));
            if (conflict)
            {
                inbox.AddEvent(Event(["https://gchat.qpic.cn/resent.png"], 3, sender: "999"), asked.AddMilliseconds(200));
                Clarification(await context.ResolveAsync("这张呢", asked.AddMilliseconds(300), default));
                Check(handler.Calls == 0, "filtered unique group bypassed conflicting replay");
                inbox.AddEvent(Event(["https://gchat.qpic.cn/resent.png"], 4, time: Now.AddSeconds(1)), Now.AddSeconds(1));
            }
            var result = await context.ResolveAsync("这张呢", Now.AddSeconds(1), default);
            Check(result.Clarification is null && result.Images.Count == 1 && handler.Calls == 1,
                "fresh confirmed image was blocked by old ambiguity");
        }
    }

    private static async Task UnsafeUrls()
    {
        var urls = new[] { "http://gchat.qpic.cn/x.png", "https://multimedia.nt.qq.com.evil.invalid/x.png",
            "https://evilqpic.cn/x.png", "https://qpic.cn.evil.invalid/x.png", "https://gchat.qpic.cn:444/x.png",
            "https://127.0.0.1/x.png", "https://user:secret@gchat.qpic.cn/x.png", "file:///C:/secret.png",
            "C:/secret.png", "data:image/png;base64,AA==", "https://gchat.qpic.cn/x.png#local-fragment" };
        foreach (var url in urls)
        {
            var handler = Handler((_, _) => Task.FromResult(PngResponse()));
            using var context = new ImageQuestionContext(Inbox(url), "123", handler);
            Clarification(await context.ResolveAsync("截图有什么", Now, default));
            Check(handler.Calls == 0, "unsafe URL reached transport");
        }
    }

    private static async Task Redirect()
    {
        var handler = Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://127.0.0.1/private?token=secret") }, Content = new ByteArrayContent([])
        }));
        using var context = new ImageQuestionContext(Inbox("https://gchat.qpic.cn/redirect.png"), "123", handler);
        Clarification(await context.ResolveAsync("这张截图是什么", Now, default));
        Check(handler.Calls == 1, "redirect was followed");
    }

    private static async Task ByteBounds()
    {
        foreach (var declared in new[] { true, false })
        {
            var handler = Handler((_, _) =>
            {
                HttpContent content = declared ? new ByteArrayContent(TinyPng)
                    : new StreamContent(new NonSeekableStream(new byte[ImageQuestionContext.MaximumImageBytes + 1]));
                if (declared) content.Headers.ContentLength = ImageQuestionContext.MaximumImageBytes + 1L;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });
            using var context = new ImageQuestionContext(Inbox("https://gchat.qpic.cn/large.png"), "123", handler);
            Clarification(await context.ResolveAsync("看图", Now, default));
        }
    }

    private static async Task ScreenshotSize()
    {
        // 1080×2400 是常见手机截图；旧上限（1024 边、1 MP）会把它判成不合规。
        var handler = Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png(1170, 2532)) }));
        using var context = new ImageQuestionContext(Inbox("https://multimedia.nt.qq.com.cn/shot.png"), "123", handler);
        var result = await context.ResolveAsync("图里是什么", Now, default);
        Check(result.Clarification is null && result.Images.Count == 1 && handler.Calls == 1, "a normal phone screenshot was rejected");
        Check(ImageQuestionContext.MaximumImageBytes >= 4 * 1024 * 1024 && ImageQuestionContext.MaximumImageEdge >= 4096,
            "size limits were lowered below the verified phone screenshot range");
    }

    private static async Task StickerFormats()
    {
        foreach (var (bytes, mime) in new[] { (Gif(245, 245), "image/gif"), (Webp(320, 240), "image/webp") })
        {
            var handler = Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            using var context = new ImageQuestionContext(Inbox("https://multimedia.nt.qq.com.cn/sticker"), "123", handler);
            var result = await context.ResolveAsync("这张图表表情包是什么", Now, default);
            Check(result.Clarification is null && result.Images.Count == 1 && result.Images[0].MimeType == mime,
                "a sticker was rejected: " + mime);
        }
        var truncated = Gif(245, 245); truncated[^1] = 0x00;
        var badHandler = Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(truncated) }));
        using var bad = new ImageQuestionContext(Inbox("https://multimedia.nt.qq.com.cn/sticker"), "123", badHandler);
        Clarification(await bad.ResolveAsync("这张图表表情包是什么", Now, default));
    }

    private static byte[] Gif(int width, int height)
    {
        var bytes = new byte[14];
        "GIF89a"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)height);
        bytes[^1] = 0x3b;
        return bytes;
    }

    private static byte[] Webp(int width, int height)
    {
        var bytes = new byte[34];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 26);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8X"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 10);
        var w = width - 1; var h = height - 1;
        bytes[24] = (byte)w; bytes[25] = (byte)(w >> 8); bytes[26] = (byte)(w >> 16);
        bytes[27] = (byte)h; bytes[28] = (byte)(h >> 8); bytes[29] = (byte)(h >> 16);
        return bytes;
    }

    private static async Task InvalidImages()
    {
        var badCrc = TinyPng.ToArray(); badCrc[29] ^= 1;
        var edge = ImageQuestionContext.MaximumImageEdge;
        var images = new[] { Encoding.UTF8.GetBytes("<html>not an image</html>"), TinyPng[..32], badCrc,
            Png(0, 1), Png((uint)edge + 1, 1), Png(4000, 4000), Png(uint.MaxValue, 1) };
        foreach (var bytes in images)
        {
            var handler = Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            using var context = new ImageQuestionContext(Inbox("https://gchat.qpic.cn/bad.png"), "123", handler);
            Clarification(await context.ResolveAsync("图里是什么", Now, default));
        }
    }

    private static async Task JpegDimensions()
    {
        foreach (var bytes in new[] { Jpeg((ushort)(ImageQuestionContext.MaximumImageEdge + 1), 1), Jpeg(1, (ushort)(ImageQuestionContext.MaximumImageEdge + 1)),
                     Jpeg(0, 1), new byte[] { 0xff, 0xd8, 0xff, 0xc0, 0, 20, 8, 0xff, 0xd9 } })
        {
            var handler = Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            using var context = new ImageQuestionContext(Inbox("https://gchat.qpic.cn/bad.jpg"), "123", handler);
            Clarification(await context.ResolveAsync("图中这件装备呢", Now, default));
        }
    }

    private static async Task NetworkFailure()
    {
        var handler = Handler((_, _) => throw new HttpRequestException("signed https://gchat.qpic.cn/x?rkey=private-token"));
        using var context = new ImageQuestionContext(Inbox("https://gchat.qpic.cn/tiny.png"), "123", handler);
        var result = await context.ResolveAsync("看图", Now, default);
        Clarification(result);
        Check(!result.Clarification!.Contains("private-token") && !result.Clarification.Contains("https://"), "transport credentials leaked");
    }

    private static async Task Deadline()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Handler(async (_, _) =>
        {
            try { await release.Task; return PngResponse(); }
            finally { exited.TrySetResult(); }
        });
        using var context = new ImageQuestionContext(Inbox("https://gchat.qpic.cn/tiny.png"), "123", handler, TimeSpan.FromMilliseconds(30));
        try { Clarification(await context.ResolveAsync("看图", Now, default)); }
        finally { release.TrySetResult(); }
        await exited.Task;
    }

    private static async Task Cancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Handler(async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return PngResponse(); });
        using var context = new ImageQuestionContext(Inbox("https://gchat.qpic.cn/tiny.png"), "123", handler);
        using var cancellation = new CancellationTokenSource();
        var resolving = context.ResolveAsync("看图", Now, cancellation.Token);
        await entered.Task;
        await cancellation.CancelAsync();
        try { await resolving; throw new Exception("cancelled question returned an image context"); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        var calls = handler.Calls;
        try { await context.ResolveAsync("看图", Now, cancellation.Token); throw new Exception("pre-cancelled question was accepted"); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Check(handler.Calls == calls, "pre-cancelled question reached transport");
    }

    private static async Task ChatAttach()
    {
        var inbox = Inbox("https://gchat.qpic.cn/a.png");
        var handler = Handler((_, _) => Task.FromResult(PngResponse()));
        using var context = new ImageQuestionContext(inbox, "123", handler);
        var first = await context.ResolveChatAsync("123", 1, false, "这是啥", Now, default);
        Check(first.Clarification is null && first.Images.Count == 1 && handler.Calls == 1, "the newest image was not attached to the first chat message");
        var second = await context.ResolveChatAsync("123", 2, false, "今天几点了", Now.AddSeconds(5), default);
        Check(second.Images.Count == 0 && handler.Calls == 1, "the same image was attached or downloaded twice");
        var asked = await context.ResolveChatAsync("123", 3, false, "这张图呢", Now.AddSeconds(6), default);
        Check(asked.Images.Count == 1 && handler.Calls == 2, "an explicit image question did not re-read the image");
        inbox.AddEvent(Event(["https://gchat.qpic.cn/b.png"], 2), Now.AddSeconds(60));
        var newer = await context.ResolveChatAsync("123", 4, false, "好吧", Now.AddSeconds(61), default);
        Check(newer.Images.Count == 1 && handler.Calls == 3, "a newly sent image was not attached");

        var privateInbox = new GroupImageInbox("123", allowedPrivateUsers: ["29070481"]);
        privateInbox.AddEvent(JsonSerializer.SerializeToElement(new { post_type = "message", message_type = "private",
            user_id = "29070481", message_id = 21, time = Now.ToUnixTimeSeconds(),
            message = new[] { new { type = "image", data = new { url = "https://multimedia.nt.qq.com/private.png" } } } }), Now);
        using var privateContext = new ImageQuestionContext(privateInbox, "123", Handler((_, _) => Task.FromResult(PngResponse())));
        var mine = await privateContext.ResolveChatAsync("private:29070481", 21, false, "看看", Now, default);
        Check(mine.Images.Count == 1, "the authorized contact's private image was not attached");
        var other = await privateContext.ResolveChatAsync("123", 22, false, "看看", Now, default);
        Check(other.Images.Count == 0, "a private image leaked into another chat");
        var plain = await privateContext.ResolveChatAsync("123", 0, false, "今天几点了", Now, default);
        Check(plain.Images.Count == 0 && plain.Clarification is null, "the voice-style path attached an image without a keyword");
    }

    private static async Task RealHosts()
    {
        var handler = Handler((request, _) =>
        {
            Check(request.RequestUri!.Host == "multimedia.nt.qq.com.cn", "a different host reached the downloader");
            return Task.FromResult(PngResponse());
        });
        var rejected = new List<string>();
        var inbox = Inbox("https://multimedia.nt.qq.com.cn/download?appid=1407&fileid=abc&rkey=redacted");
        using var context = new ImageQuestionContext(inbox, "123", handler, rejectedHost: host => rejected.Add(host));
        var result = await context.ResolveChatAsync("123", 1, false, "看看这张", Now, default);
        Check(result.Clarification is null && result.Images.Count == 1 && handler.Calls == 1, "the real QQ image host was rejected");
        Check(rejected.Count == 0, "an accepted host was reported as rejected");

        var blockedDownloads = 0;
        using var blocked = new ImageQuestionContext(Inbox("https://evil.example/x.png"), "123",
            Handler((_, _) => { Interlocked.Increment(ref blockedDownloads); return Task.FromResult(PngResponse()); }), rejectedHost: host => rejected.Add(host));
        var denied = await blocked.ResolveChatAsync("123", 1, false, "看看这张", Now, default);
        Check(denied.Clarification is not null && denied.Images.Count == 0 && blockedDownloads == 0, "a non-QQ host was downloaded");
        Check(rejected.SequenceEqual(["evil.example"]), "the rejected host was not reported");
    }

    private static async Task NewestForSaving()
    {
        var stamp = DateTimeOffset.UtcNow;
        var inbox = new GroupImageInbox("123");
        inbox.AddEvent(Event(["https://gchat.qpic.cn/old.png"], 1, time: stamp.AddSeconds(-30)), stamp);
        inbox.AddEvent(Event(["https://gchat.qpic.cn/new.png"], 2, time: stamp), stamp);
        var handler = Handler((_, _) => Task.FromResult(PngResponse()));
        using var context = new ImageQuestionContext(inbox, "123", handler);
        var first = await context.ResolveNewestAsync("123", default);
        var again = await context.ResolveNewestAsync("123", default);
        Check(first is not null && again is not null && handler.Calls == 2, "the newest image was not resolved twice for saving");
        Check(first!.DataUrl == "data:image/png;base64," + Convert.ToBase64String(TinyPng), "saving resolved different bytes");
        Check(await context.ResolveNewestAsync("999", default) is null && handler.Calls == 2, "another chat reached the saving downloader");
        var unsafeInbox = new GroupImageInbox("123");
        unsafeInbox.AddEvent(Event(["https://evil.example.com/x.png"], 3, time: stamp), stamp);
        using var unsafeContext = new ImageQuestionContext(unsafeInbox, "123", handler);
        Check(await unsafeContext.ResolveNewestAsync("123", default) is null && handler.Calls == 2,
            "an unlisted host reached the saving downloader");
    }

    private static GroupImageInbox Inbox(params string[] urls) => Inbox(urls, Now);
    private static GroupImageInbox Inbox(string url, DateTimeOffset time) => Inbox([url], time);
    private static GroupImageInbox Inbox(string[] urls, DateTimeOffset time)
    {
        var inbox = new GroupImageInbox("123");
        inbox.AddEvent(Event(urls, time: time), Now);
        return inbox;
    }

    private static JsonElement Event(string[] urls, long message = 1, string sender = "456", DateTimeOffset? time = null) =>
        JsonSerializer.SerializeToElement(new { post_type = "message", message_type = "group", group_id = "123", user_id = sender,
            message_id = message, time = (time ?? Now).ToUnixTimeSeconds(),
            message = urls.Select(url => new { type = "image", data = new { url } }) });
    private static HttpResponseMessage PngResponse() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(TinyPng) };
    private static FakeHandler Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) => new(callback);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Clarification(ImageQuestionContextResult result) =>
        Check(!string.IsNullOrWhiteSpace(result.Clarification) && result.Images.Count == 0, "invalid images were attached or clarification missing");

    private static byte[] Png(uint width, uint height)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zlib.Write(new byte[] { 0, 255, 0, 0, 255 });
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string kind, byte[] data)
    {
        var chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(kind).CopyTo(chunk, 4); data.CopyTo(chunk, 8);
        uint crc = uint.MaxValue;
        foreach (var value in chunk.AsSpan(4, data.Length + 4))
        {
            var entry = (crc ^ value) & 255;
            for (var bit = 0; bit < 8; bit++) entry = (entry & 1) == 1 ? 0xedb88320u ^ (entry >> 1) : entry >> 1;
            crc = entry ^ (crc >> 8);
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), ~crc);
        output.Write(chunk);
    }

    // Structural JPEG fixture; used only to verify rejection before any decoder or model is invoked.
    private static byte[] Jpeg(ushort width, ushort height) =>
        [0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
            1, 1, 0x11, 0, 0xff, 0xda, 0, 8, 1, 1, 0, 0, 63, 0, 1, 0xff, 0xd9];

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref calls); return callback(request, ct); }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
}