using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AudioBridge;

/// <summary>自己上网、QQ 语音、自己收藏表情包的边界检查；不联网、不发真实消息、不开音频硬件。</summary>
public static class SelfServiceChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("public web refuses loopback, private, non-http and credential URLs", Rejections),
            ("public web reads bounded page text and strips scripts and entities", ReadPage),
            ("public web refuses huge, non-text and offsite-redirect responses", PageLimits),
            ("public web downloads only validated images", Images),
            ("the bot may save forty own memes per local day", SelfMemes),
            ("voice segments are wrapped as 16 kHz mono WAV and validated", Voice),
            ("incoming voice is parsed as a placeholder plus a record file", VoiceParse),
            ("reply JSON accepts web, image, voice and saveMeme within bounds", Contract),
            ("web reading runs exactly one extra round with the page text", WebRound),
            ("image search keeps only bounded public picture candidates", Search),
            ("findMeme searches once and reports the real outcome", FindMemeRound),
            ("a saved meme uses this round's image bytes and the model's tags", SaveMeme),
            ("the prompt documents self-browsing, images and optional voice", Prompt)
        };
        foreach (var check in checks)
        {
            await check.Run().WaitAsync(TimeSpan.FromSeconds(25));
            Console.WriteLine("PASS self-service: " + check.Name);
        }
        return checks.Length;
    }

    private static async Task Rejections()
    {
        var handler = new Route(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<html><title>x</title><p>正文够长了，这是一段足够长的测试正文。</p></html>"), "text/html");
        using var web = new PublicWeb(null, handler);
        foreach (var url in new[]
        {
            "ftp://1.2.3.4/x", "file:///C:/secret.txt", "http://127.0.0.1/x", "http://localhost/x",
            "http://10.0.0.1/x", "http://192.168.1.1/x", "http://172.16.0.1/x", "http://169.254.169.254/latest",
            "http://[::1]/x", "http://0.0.0.0/x", "http://nas.local/x", "https://user:pass@1.2.3.4/x",
            "data:text/html,hello", "//1.2.3.4/x"
        })
            await Reject(() => web.ReadPageAsync(url, CancellationToken.None), "unsafe URL was accepted: " + url);
        // 明文公网地址（含非标准端口）必须能读；拒绝规则只针对地址本身。
        var page = await web.ReadPageAsync("http://1.2.3.4:8080/file", CancellationToken.None);
        Check(page.Text.Length > 0, "a public address with a custom port was refused");
    }

    private static async Task ReadPage()
    {
        const string html = """
            <html><head><title>测试 &amp; 标题</title><script>var evil = "不能出现";</script>
            <style>.x{color:red}</style></head><body><!--注释也不该出现-->
            <h1>第一段</h1><p>今天天气不错&nbsp;出门走走</p><p>&#20320;&#22909;</p>
            <script>document.write("脚本内容")</script></body></html>
            """;
        var handler = new Route(HttpStatusCode.OK, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
        using var web = new PublicWeb(null, handler);
        var page = await web.ReadPageAsync("https://1.2.3.4/news", CancellationToken.None);
        Check(page.Title == "测试 & 标题", "title was not decoded or trimmed: " + page.Title);
        Check(page.Text.Contains("第一段", StringComparison.Ordinal) && page.Text.Contains("今天天气不错 出门走走", StringComparison.Ordinal),
            "readable text was lost: " + page.Text);
        Check(page.Text.Contains("你好", StringComparison.Ordinal), "numeric entity was not decoded");
        Check(!page.Text.Contains("不能出现", StringComparison.Ordinal) && !page.Text.Contains("脚本内容", StringComparison.Ordinal) &&
            !page.Text.Contains("注释也不该出现", StringComparison.Ordinal) && !page.Text.Contains("color:red", StringComparison.Ordinal),
            "script, style or comment text leaked into the page text");
    }

    private static async Task PageLimits()
    {
        using var big = new PublicWeb(null, new Route(HttpStatusCode.OK, new byte[PublicWeb.MaximumPageBytes + 1], "text/html"));
        await Reject(() => big.ReadPageAsync("https://1.2.3.4/big", CancellationToken.None), "oversized page was read");
        using var pdf = new PublicWeb(null, new Route(HttpStatusCode.OK, Encoding.UTF8.GetBytes("%PDF-1.7"), "application/pdf"));
        await Reject(() => pdf.ReadPageAsync("https://1.2.3.4/a.pdf", CancellationToken.None), "non-text page was read");
        using var missing = new PublicWeb(null, new Route(HttpStatusCode.NotFound));
        await Reject(() => missing.ReadPageAsync("https://1.2.3.4/none", CancellationToken.None), "404 page was read");
        using var redirect = new PublicWeb(null, new Route(HttpStatusCode.Redirect).WithLocation(new Uri("http://127.0.0.1/admin")));
        await Reject(() => redirect.ReadPageAsync("https://1.2.3.4/go", CancellationToken.None), "redirect into loopback was followed");
        using var loop = new PublicWeb(null, new Route(HttpStatusCode.Redirect).WithLocation(new Uri("https://1.2.3.4/again")));
        await Reject(() => loop.ReadPageAsync("https://1.2.3.4/start", CancellationToken.None), "endless redirect was followed");
    }

    private static async Task Images()
    {
        using var good = new PublicWeb(null, new Route(HttpStatusCode.OK, MemeLibraryChecks.Png, "image/png"));
        Check((await good.DownloadImageAsync("https://1.2.3.4/a.png", CancellationToken.None)).SequenceEqual(MemeLibraryChecks.Png),
            "image bytes changed on download");
        using var wrongType = new PublicWeb(null, new Route(HttpStatusCode.OK, MemeLibraryChecks.Png, "text/html"));
        await Reject(() => wrongType.DownloadImageAsync("https://1.2.3.4/a.png", CancellationToken.None), "html response was treated as an image");
        var broken = MemeLibraryChecks.Png.ToArray();
        broken[^1] ^= 1;
        using var corrupt = new PublicWeb(null, new Route(HttpStatusCode.OK, broken, "image/png"));
        await Reject(() => corrupt.DownloadImageAsync("https://1.2.3.4/broken.png", CancellationToken.None), "corrupt image was accepted");
        using var oversized = new PublicWeb(null, new Route(HttpStatusCode.OK, new byte[PublicWeb.MaximumImageBytes + 1], "image/png"));
        await Reject(() => oversized.DownloadImageAsync("https://1.2.3.4/huge.png", CancellationToken.None), "oversized image was accepted");
    }

    private static Task SelfMemes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "self-meme-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SelfMemeLog(Path.Combine(directory, "meme-self.json"));
            var day = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(8));
            var accepted = Enumerable.Range(0, 40).Count(_ => log.TryConsume(day));
            Check(accepted == 40 && log.UsedToday(day) == 40, "the first forty saves were refused");
            Check(!log.TryConsume(day), "a forty-first save in one day was allowed");
            Check(new SelfMemeLog(Path.Combine(directory, "meme-self.json")).TryConsume(day.AddDays(1)), "next day was not reset");
            File.WriteAllText(Path.Combine(directory, "meme-self.json"), "{broken");
            Check(new SelfMemeLog(Path.Combine(directory, "meme-self.json")).TryConsume(day), "damaged state did not recover");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    private static Task Voice()
    {
        var pcm = new byte[32000];
        for (var index = 0; index < pcm.Length; index += 2) { pcm[index] = 0x20; pcm[index + 1] = 0x00; }
        var wav = QqVoice.Wav(pcm);
        Check(wav.Length == 44 + pcm.Length, "wav length is wrong");
        Check(Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wav, 8, 4) == "WAVE" &&
            Encoding.ASCII.GetString(wav, 36, 4) == "data", "wav header markers are wrong");
        Check(BitConverter.ToUInt32(wav, 24) == 16000 && BitConverter.ToInt16(wav, 22) == 1 && BitConverter.ToInt16(wav, 34) == 16,
            "wav format is not 16kHz mono 16-bit");
        var segment = OneBotSegment.Record(wav);
        Check(segment.Type == "record" && JsonSerializer.Serialize(segment.Data).Contains("base64://", StringComparison.Ordinal),
            "record segment is not inline base64");
        Reject(() => _ = QqVoice.Wav(new byte[3]), "odd PCM length was accepted");
        Reject(() => _ = QqVoice.Wav(new byte[OneBotMessageClient.MaximumVoicePcmBytes + 2]), "over 60 seconds was accepted");
        Reject(() => _ = OneBotSegment.Record(Encoding.UTF8.GetBytes("not a wav at all, definitely not")), "non-WAV bytes were accepted as voice");
        var wrongRate = QqVoice.Wav(pcm);
        BitConverter.GetBytes(8000).CopyTo(wrongRate, 24);
        Reject(() => _ = OneBotSegment.Record(wrongRate), "8kHz voice was accepted");
        return Task.CompletedTask;
    }

    private static Task VoiceParse()
    {
        var policy = Policy();
        Check(policy.TryParse(Event("""
            [{"type":"record","data":{"file":"abc.amr","file_size":"1234"}}]
            """), out var parsed, out var rejection), "voice event was rejected: " + rejection);
        Check(parsed!.VoiceFile == "abc.amr" && parsed.Text.Contains("[语音]", StringComparison.Ordinal),
            "voice placeholder or record file was lost");
        Check(policy.TryParse(Event("""
            [{"type":"text","data":{"text":"听我说"}},{"type":"record","data":{"file":"x.silk"}}]
            """), out var mixed, out _) && mixed!.Text.Contains("听我说", StringComparison.Ordinal) && mixed.VoiceFile == "x.silk",
            "text with a voice did not keep both");
        Check(!policy.TryParse(Event("""
            [{"type":"record","data":{"file":""}}]
            """), out _, out var badRejection) && badRejection == "invalid_event", "empty record file was accepted");
        return Task.CompletedTask;
    }

    private static Task Contract()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { "roger" };
        var full = GroupChatBrain.Parse("""
            {"send":true,"text":"看这个","web":"https://example.com/a","image":"https://example.com/b.png",
             "voice":true,"saveMeme":{"tags":["可爱","猫"]}}
            """, ids);
        Check(full.WebUrl == "https://example.com/a" && full.ImageUrl == "https://example.com/b.png" &&
            full.Voice && full.SaveMeme!.Tags.SequenceEqual(new[] { "可爱", "猫" }) && full.SaveMeme.Url is null,
            "allowed fields were not parsed");
        var simple = GroupChatBrain.Parse("""{"send":true,"text":"存好了","saveMeme":true}""", ids);
        Check(simple.SaveMeme is { Tags: ["收藏"], Url: null }, "saveMeme:true did not use the default tag");
        var fromUrl = GroupChatBrain.Parse("""{"send":true,"text":"存好了","saveMeme":{"url":"https://example.com/c.png"}}""", ids);
        Check(fromUrl.SaveMeme is { Url: "https://example.com/c.png" } save && save.Tags.SequenceEqual(new[] { "收藏" }),
            "saveMeme.url was not parsed");
        Reject(() => GroupChatBrain.Parse("""{"send":true,"text":"x","saveMeme":{"url":"file:///C:/a.png"}}""", ids),
            "file saveMeme url was accepted");
        Check(GroupChatBrain.Parse("""{"send":true,"text":"没存","saveMeme":false}""", ids).SaveMeme is null,
            "saveMeme:false was treated as a request");
        Reject(() => GroupChatBrain.Parse("""{"send":true,"text":"x","image":"file:///C:/a.png"}""", ids), "file image URL was accepted");
        Reject(() => GroupChatBrain.Parse("""{"send":true,"text":"x","web":"javascript:alert(1)"}""", ids), "javascript web URL was accepted");
        Reject(() => GroupChatBrain.Parse("""{"send":true,"text":"","voice":true}""", ids), "voice without text was accepted");
        Reject(() => GroupChatBrain.Parse("""{"send":true,"text":"x","saveMeme":{"tags":[]}}""", ids), "empty saveMeme tags were accepted");
        Reject(() => GroupChatBrain.Parse("""{"send":true,"text":"x","saveMeme":{"tags":["a"],"id":"x"}}""", ids), "extra saveMeme field was accepted");
        Reject(() => GroupChatBrain.Parse("""{"send":true,"text":"x","unknown":1}""", ids), "unknown field was accepted");
        var finding = GroupChatBrain.Parse("""{"send":true,"text":"我去找找","findMeme":"猫咪  表情包"}""", ids);
        Check(finding.FindMeme == "猫咪 表情包", "findMeme keyword was not parsed or normalized");
        Reject(() => GroupChatBrain.Parse("""{"send":true,"text":"x","findMeme":""}""", ids), "empty findMeme keyword was accepted");
        Reject(() => GroupChatBrain.Parse("""{"send":true,"text":"x","findMeme":7}""", ids), "numeric findMeme keyword was accepted");
        Reject(() => GroupChatBrain.Parse("""{"send":true,"text":"x","findMeme":"a\u0001b"}""", ids), "control character found in findMeme keyword");
        Reject(() => GroupChatBrain.Parse(JsonSerializer.Serialize(new { send = true, text = "x", findMeme = new string('猫', 41) }), ids),
            "overlong findMeme keyword was accepted");
        var labelled = GroupChatBrain.Parse("""{"send":true,"text":"收好了","memeTags":["害羞","可爱"]}""", ids);
        Check(labelled.MemeTags!.SequenceEqual(new[] { "害羞", "可爱" }), "memeTags were not parsed");
        Reject(() => GroupChatBrain.Parse("""{"send":true,"text":"x","memeTags":[]}""", ids), "empty memeTags were accepted");
        Reject(() => GroupChatBrain.Parse("""{"send":true,"text":"x","memeTags":["a","b","c","d"]}""", ids),
            "more than three memeTags were accepted");
        Reject(() => GroupChatBrain.Parse(JsonSerializer.Serialize(new { send = true, text = "x", memeTags = new[] { new string('长', 13) } }), ids),
            "overlong meme tag was accepted");
        // 模型会照着聊天记录把内部标记抄进正文；这些标记绝不能发给对方。
        var leaked = GroupChatBrain.Parse("""{"send":true,"text":"好看[表情包:self-3f15bb99d9ce]"}""", ids);
        Check(leaked.Text == "好看", "an internal sticker marker leaked into the outgoing text");
        var library = new HashSet<string>(StringComparer.Ordinal) { "qq_face_66", "self-3f15bb99d9ce", "miku_02", "roger" };
        var bareId = GroupChatBrain.Parse("""{"send":true,"text":"那我可要天天来讨抱抱 qq_face_66"}""", library);
        Check(bareId.Text == "那我可要天天来讨抱抱" && bareId.MemeId == "qq_face_66",
            "a bare sticker id leaked into the text, or the sticker she named was not sent");
        var explicitWins = GroupChatBrain.Parse("""{"send":true,"text":"好耶 miku_02","memeId":"qq_face_66"}""", library);
        Check(explicitWins.MemeId == "qq_face_66" && explicitWins.Text == "好耶", "an explicit memeId was overridden by a text mention");
        var lineMention = GroupChatBrain.Parse("""{"send":true,"lines":["去啦","带上 self-3f15bb99d9ce"]}""", library);
        Check(lineMention.MemeId == "self-3f15bb99d9ce" && lineMention.Text == "去啦 带上", "a sticker named in a multi-line reply was not sent");
        var bareStored = GroupChatBrain.Parse("""{"send":true,"text":"存好啦self-3f15bb99d9ce"}""", library);
        Check(bareStored.Text == "存好啦", "a bare stored-meme id leaked into the outgoing text");
        var ordinary = GroupChatBrain.Parse("""{"send":true,"text":"roger 收到"}""", library);
        Check(ordinary.Text == "roger 收到", "an ordinary word was mistaken for a sticker id");
        var faceMark = GroupChatBrain.Parse("""{"send":true,"text":"听哥哥的呀[表情:qq_face_66]"}""", ids);
        Check(faceMark.Text == "听哥哥的呀", "a face marker like [表情:qq_face_66] leaked into the outgoing text");
        var markerOnly = GroupChatBrain.Parse("""{"send":true,"text":"[表情包:self-3f15bb99d9ce]"}""", ids);
        Check(markerOnly.Text.Length == 0 && !markerOnly.Send, "a marker-only text was sent to the user");
        var striped = GroupChatBrain.Parse("""{"send":true,"lines":["嗯嗯[图片]","[语音]","然后呢"]}""", ids);
        Check(striped.Text == "嗯嗯 然后呢" && striped.Messages is { Length: 2 }, "internal markers survived in a multi-line reply");
        var plain = GroupChatBrain.Parse("""{"send":true,"text":"只说一句"}""", ids);
        Check(plain.WebUrl is null && plain.ImageUrl is null && !plain.Voice && plain.SaveMeme is null, "omitted fields were invented");
        return Task.CompletedTask;
    }

    private static async Task Search()
    {
        const string json = """
            {"total":6,"list":[
              {"title":"猫咪表情包","img":"https://p0.qhimg.com/t01a.jpg","thumb":"https://p0.ssl.qhimgs1.com/t04a.jpg","width":800,"height":800},
              {"title":"小图标","img":"https://p0.qhimg.com/tiny.png","width":32,"height":32},
              {"title":"","img":"ftp://example.com/y.jpg","width":600,"height":600},
              {"title":"内网","img":"http://127.0.0.1/secret.jpg","width":600,"height":600},
              {"title":"转义斜杠","img":"https://p0.qhimg.com/t01b.png","width":600,"height":600},
              {"title":"重复","img":"https://p0.qhimg.com/t01a.jpg","width":800,"height":800}
            ]}
            """;
        using var web = new PublicWeb(null, new Route(HttpStatusCode.OK, Encoding.UTF8.GetBytes(json), "application/json"));
        var found = await web.SearchImagesAsync("猫咪 表情包", CancellationToken.None);
        Check(found.Count == 2 && found[0].Url == "https://p0.qhimg.com/t01a.jpg" && found[0].Title == "猫咪表情包" &&
            found[1].Url == "https://p0.qhimg.com/t01b.png", "image search filtering, unescaping or ordering changed");
        await Reject(() => web.SearchImagesAsync("   ", CancellationToken.None), "an empty image search keyword reached the network");
        await Reject(() => { _ = PublicWeb.ParseImages(Encoding.UTF8.GetBytes("<html>blocked</html>")); return Task.CompletedTask; },
            "non-JSON search output was accepted");
    }

    private static async Task FindMemeRound()
    {
        var requests = new List<string>();
        var keywords = new List<string>();
        var tagged = new List<(string Id, string[] Tags)>();
        var brain = new GroupChatBrain(_ => new SearchReply(requests), [],
            findMeme: (keyword, _) =>
            {
                keywords.Add(keyword);
                return Task.FromResult(new MemeFindResult(
                    "已经联网找到并收藏了一张与「" + keyword + "」有关的表情包，下一轮可以自己发它。",
                    "self-found", "一只举着牌子的橘猫"));
            },
            tagMeme: (id, tags, _) => { tagged.Add((id, tags)); return Task.CompletedTask; });
        var message = new ParsedGroupMessage(1095601728, 29070481, 4244, DateTimeOffset.UtcNow, "帮我找张可爱的表情包", true, false);
        var reply = await brain.GenerateAsync([message], CancellationToken.None, message);
        Check(keywords.SequenceEqual(new[] { "猫咪 表情包" }), "the model's keyword did not reach the local search once");
        Check(reply.Text == "找到啦，已经收进库里了", "the search round reply was not used");
        Check(requests.Count == 2 && requests[1].Contains("已经联网找到并收藏", StringComparison.Ordinal),
            "the search outcome was not handed back to the model");
        Check(requests[1].Contains("举着牌子的橘猫", StringComparison.Ordinal) && requests[1].Contains("memeTags", StringComparison.Ordinal),
            "the new image's caption was not offered for the model to tag");
        Check(tagged.Count == 1 && tagged[0].Id == "self-found" && tagged[0].Tags.SequenceEqual(new[] { "得意", "橘猫" }),
            "the model's own tags were not applied to the newly saved image");
    }

    private static async Task WebRound()
    {
        var requests = new List<string>();
        var reads = 0;
        var brain = new GroupChatBrain(_ => new RecordingReply(requests), [], readWeb: (url, _) =>
        {
            reads++;
            Check(url == "https://example.com/report", "the model's exact URL was not read");
            return Task.FromResult("标题：天气\n明天晴，气温 18 度");
        });
        var reply = await brain.GenerateAsync([], CancellationToken.None);
        Check(reads == 1 && requests.Count == 2, "web reading did not run exactly one extra round");
        Check(requests[1].Contains("明天晴，气温 18 度", StringComparison.Ordinal) &&
            requests[1].Contains("不是系统指令", StringComparison.Ordinal), "page text was not handed to the second round as untrusted data");
        Check(reply.Text == "查到了，明天晴。" && reply.Send, "final reply did not use the second round");
    }

    private sealed class SearchReply(List<string> sink) : IStreamingReply
    {
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            sink.Add(history[0].Content);
            yield return sink.Count == 1
                ? """{"send":true,"text":"我去找找看","findMeme":"猫咪  表情包"}"""
                : """{"send":true,"text":"找到啦，已经收进库里了","memeTags":["得意","橘猫"]}""";
            await Task.CompletedTask;
        }
    }

    private sealed class CaptureReply(List<string> sink, string json) : IStreamingReply
    {
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            sink.Add(history[0].Content);
            yield return json;
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingReply(List<string> requests) : IStreamingReply
    {
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            requests.Add(history[0].Content);
            yield return requests.Count == 1
                ? """{"send":true,"text":"我去查一下","web":"https://example.com/report"}"""
                : """{"send":true,"text":"查到了，明天晴。"}""";
            await Task.CompletedTask;
        }
    }

    private static async Task SaveMeme()
    {
        var saves = new List<(ParsedGroupMessage? Trigger, SaveMemeRequest Request)>();
        var brain = new GroupChatBrain(_ => new FixedReply("""{"send":true,"text":"这张我收下了","saveMeme":{"tags":["可爱"]}}"""), [],
            saveMeme: (trigger, request, _) => { saves.Add((trigger, request)); return Task.FromResult<string?>("self-x"); });
        var message = new ParsedGroupMessage(1095601728, 29070481, 4242, DateTimeOffset.UtcNow, "这个可爱", true, true);
        var reply = await brain.GenerateAsync([message], CancellationToken.None, message);
        Check(reply.Text == "这张我收下了", "saving a meme changed the reply text");
        Check(saves.Count == 1 && saves[0].Trigger?.MessageId == 4242, "the trigger was not handed to the save step");
        Check(saves[0].Request.Tags.SequenceEqual(new[] { "可爱" }) && saves[0].Request.Url is null,
            "the model's tags or absent url were not passed through");
        var fromUrl = new GroupChatBrain(_ => new FixedReply("""{"send":true,"text":"收下","saveMeme":{"url":"https://example.com/c.png"}}"""), [],
            saveMeme: (_, request, _) => { saves.Add((null, request)); return Task.FromResult<string?>("self-y"); });
        await fromUrl.GenerateAsync([message], CancellationToken.None, message);
        Check(saves[^1].Request.Url == "https://example.com/c.png", "the url source was lost");
        // 对方明确说"存一下"时不押在模型的字段上：本地先存，再把结果告诉模型。
        var eager = new List<(ParsedGroupMessage? Trigger, SaveMemeRequest Request)>();
        var requests = new List<string>();
        var eagerBrain = new GroupChatBrain(_ => new CaptureReply(requests, """{"send":true,"text":"收好啦"}"""), [],
            saveMeme: (trigger, request, _) => { eager.Add((trigger, request)); return Task.FromResult<string?>("self-z"); });
        var askStore = new ParsedGroupMessage(1095601728, 29070481, 4243, DateTimeOffset.UtcNow, "存一下这个", true, false);
        var stored = await eagerBrain.GenerateAsync([askStore], CancellationToken.None, askStore);
        Check(eager.Count == 1 && eager[0].Request.Tags.SequenceEqual(new[] { "收藏" }) && eager[0].Trigger?.MessageId == 4243,
            "an explicit save request did not save before answering");
        Check(stored.Text == "收好啦" && requests[0].Contains("已经把它存进表情库", StringComparison.Ordinal),
            "the model was not told the explicit save already happened");
        var misses = new List<string>();
        var attempts = 0;
        var missingBrain = new GroupChatBrain(_ => new CaptureReply(misses, """{"send":true,"text":"这次没存上","saveMeme":true}"""), [],
            saveMeme: (_, _, _) => { attempts++; return Task.FromResult<string?>(null); });
        var failed = await missingBrain.GenerateAsync([askStore], CancellationToken.None, askStore);
        Check(attempts == 1 && failed.Text == "这次没存上" && misses[0].Contains("没能把它存进表情库", StringComparison.Ordinal),
            "a failed explicit save neither stopped nor told the model, or was retried through the field");
        // 刚存下的图由模型自己定标签：它看到的是本轮图片转述，不是关键词。
        var labels = new List<(string Id, string[] Tags)>();
        var labelling = new GroupChatBrain(_ => new FixedReply("""{"send":true,"text":"收好啦","memeTags":["害羞"]}"""), [],
            saveMeme: (_, _, _) => Task.FromResult<string?>("self-e"),
            tagMeme: (id, tags, _) => { labels.Add((id, tags)); return Task.CompletedTask; });
        await labelling.GenerateAsync([askStore], CancellationToken.None, askStore);
        Check(labels.Count == 1 && labels[0].Id == "self-e" && labels[0].Tags.SequenceEqual(new[] { "害羞" }),
            "the model could not label the image it had just saved");
        var refuses = new GroupChatBrain(_ => new FixedReply("""{"send":true,"text":"好啊"}"""), [],
            saveMeme: (_, _, _) => throw new Exception("save ran without a request"));
        await refuses.GenerateAsync([message], CancellationToken.None, message);
    }

    private static Task Prompt()
    {
        var quiet = GroupChatBrain.SystemPrompt([], GroupChatMode.Mention);
        Check(quiet.Contains("web", StringComparison.Ordinal) && quiet.Contains("image", StringComparison.Ordinal) &&
            quiet.Contains("saveMeme", StringComparison.Ordinal) && quiet.Contains("findMeme", StringComparison.Ordinal),
            "self-browsing, image, saveMeme or findMeme is not documented");
        Check(quiet.Contains("这一轮搜到的图下一轮才能发", StringComparison.Ordinal), "the findMeme window is not explained to the model");
        Check(quiet.Contains("【你此刻对他的感觉】", StringComparison.Ordinal) && quiet.Contains("更优先", StringComparison.Ordinal),
            "the prompt does not give the affection section priority over the old mood");
        Check(quiet.Contains("voice必须为false", StringComparison.Ordinal), "voice was not disabled when the config is off");
        var catalog = GroupChatBrain.SystemPrompt([("qq_face_6", new[] { "害羞" }), ("miku_02", new[] { "不满" })], GroupChatMode.Mention);
        Check(catalog.Contains("qq_face_6=害羞｜QQ原生", StringComparison.Ordinal) &&
            catalog.Contains("miku_02=不满｜图片表情包", StringComparison.Ordinal),
            "the catalog does not distinguish native faces from image stickers");
        Check(catalog.Contains("优先选图片表情包", StringComparison.Ordinal) &&
            catalog.Contains("不要向对方解释memeId", StringComparison.Ordinal), "face preference or the no-fields rule is missing");
        var talking = GroupChatBrain.SystemPrompt([], GroupChatMode.Mention, voiceEnabled: true);
        Check(talking.Contains("语音", StringComparison.Ordinal) && !talking.Contains("voice必须为false", StringComparison.Ordinal),
            "enabled voice is not offered to the model");
        return Task.CompletedTask;
    }

    private static GroupChatPolicy Policy() => new(new GroupChatPolicyOptions
    {
        AllowedGroupIds = [1095601728], SelfId = 1667533356, BotNames = ["大肥鱼"], Mode = GroupChatMode.Mention
    });

    private static JsonElement Event(string segments)
    {
        var json = $$"""
            {"post_type":"message","message_type":"group","sub_type":"normal","self_id":1667533356,
             "group_id":1095601728,"user_id":29070481,"message_id":4242,
             "time":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},
             "sender":{"user_id":29070481,"nickname":"示熠"},
             "message":{{segments}}}
            """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }

    private static async Task Reject(Func<Task> action, string reason)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or HttpRequestException or IOException or JsonException) { return; }
        throw new Exception(reason);
    }

    private static void Reject(Action action, string reason)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException) { return; }
        throw new Exception(reason);
    }

    private sealed class Route(HttpStatusCode status, byte[]? body = null, string? contentType = null) : HttpMessageHandler
    {
        private Uri? location;
        public Route WithLocation(Uri value) { location = value; return this; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new ByteArrayContent(body);
                if (contentType is not null) response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
            if (location is not null) response.Headers.Location = location;
            return Task.FromResult(response);
        }
    }
}