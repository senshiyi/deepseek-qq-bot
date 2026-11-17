using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AudioBridge;
using NAudio.Wave;

public static class BreezeTtsChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("request carries the saved voice, instructions and format", RequestShape),
            ("24 kHz audio becomes the exact 16 kHz WAV the QQ voice path accepts", Resample),
            ("wav responses decode and a mislabelled wav is never read as mp3", WavAndMislabelled),
            ("real mp3 responses are decoded when the service really sends mp3", Mp3Response),
            ("emotion changes only the instruction, never the voice id", EmotionSwitch),
            ("no saved voice means voice design instead of a made up id", DesignMode),
            ("a busy server is retried and only then given up", BusyRetry),
            ("bad responses, overlong audio and repeated failures are rejected fast", Rejections),
            ("breeze failures fall back to the edge voice", Fallback),
            ("fallback off surfaces the breeze failure instead of staying silent", FallbackOff),
            ("the tone falls back to the saved contact state when the model omits it", ToneFromState),
            ("settings, engine selection and reply parsing stay consistent", SettingsAndParsing),
            ("an explicit voice request is answered with voice even if the model says text", VoiceOverride),
            ("the responder sends voice only, with no duplicate text", ResponderFlow),
            ("a slow voice is awaited and text is not sent as well", VoiceOnlyReply),
            ("a real loopback request reaches the service and returns a WAV", LoopbackRequest),
            ("every synthesized voice is archived locally with its script", ArchiveVoice),
            ("voice archives older than seven days are cleaned up", ArchiveCleanup)
        };
        foreach (var check in checks) { await check.Run(); Console.WriteLine("PASS breeze tts: " + check.Name); }
        return checks.Length;
    }

    private static async Task RequestShape()
    {
        string? body = null;
        var handler = new Handler(async (request, ct) =>
        {
            Check(request.Method == HttpMethod.Post, "wrong method");
            Check(request.RequestUri?.ToString() == "http://127.0.0.1:8080/v1/audio/speech", "wrong endpoint");
            Check(request.Content?.Headers.ContentType?.MediaType == "application/json", "wrong content type");
            body = await request.Content!.ReadAsStringAsync(ct);
            return PcmResponse(0.2);
        });
        using var client = Client(handler);
        var wav = await client.SpeechWavAsync("你好呀", "happy", default);
        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Check(root.GetProperty("input").GetString() == "你好呀", "text was not sent");
        Check(root.GetProperty("voice").GetString() == "dafeiyu", "saved voice id was not sent");
        Check(root.GetProperty("response_format").GetString() == "pcm", "response format was not sent");
        var instruction = root.GetProperty("instructions").GetString() ?? "";
        Check(instruction.Contains("声音年轻", StringComparison.Ordinal) && instruction.Contains("笑意", StringComparison.Ordinal),
            "instructions lost the voice description or the emotion");
        Check(handler.Calls == 1 && wav.Length > 44, "no audio came back");
    }

    private static async Task Resample()
    {
        using var client = Client(new Handler((_, _) => Task.FromResult(PcmResponse(0.2))));
        var wav = await client.SpeechWavAsync("测试一下", null, default);
        var info = WavInfo(wav);
        Check(info.Rate == 16000 && info.Channels == 1 && info.Bits == 16, "audio was not converted to 16 kHz mono 16 bit");
        Check(info.Data is >= 6000 and <= 7000, "duration is not about 0.2 s after resampling");
        Check(wav.Length == 44 + info.Data && wav[44..].Any(value => value != 0), "resampled audio is silent or mis-framed");
        // QQ 语音段只收这一种 WAV；能过这一关就说明转码结果可以直接发。
        _ = OneBotSegment.Record(wav);
    }

    private static async Task WavAndMislabelled()
    {
        var source = Pcm(24000, 0.2);
        var wavBytes = QqVoice.Wav(source, 24000);
        foreach (var media in new[] { "audio/wav", "audio/mpeg" })
        {
            using var client = Client(new Handler((_, _) => Task.FromResult(AudioResponse(wavBytes, media, 24000))));
            var wav = await client.SpeechWavAsync("标错了也照样认", null, default);
            var info = WavInfo(wav);
            Check(info.Rate == 16000 && info.Data > 1000, media + " was not decoded as the wav it really is");
        }
    }

    private static async Task Mp3Response()
    {
        var pcm = Pcm(24000, 0.4);
        using var encoded = new MemoryStream();
        using (var source = new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(24000, 16, 1)))
            MediaFoundationEncoder.EncodeToMp3(source, encoded, 64000);
        var mp3 = encoded.ToArray();
        Check(mp3.Length > 100, "the local mp3 encoder produced nothing");
        string? body = null;
        var handler = new Handler(async (request, ct) =>
        {
            body = await request.Content!.ReadAsStringAsync(ct);
            return AudioResponse(mp3, "audio/mpeg", 24000);
        });
        using var client = new BreezeTtsClient("http://127.0.0.1:8080", "dafeiyu", "声音年轻", "mp3", TimeSpan.FromSeconds(20), handler);
        var wav = await client.SpeechWavAsync("mp3 也算音频", "happy", default);
        using var json = JsonDocument.Parse(body!);
        Check(json.RootElement.GetProperty("response_format").GetString() == "mp3", "mp3 was not requested");
        var info = WavInfo(wav);
        Check(info.Rate == 16000 && info.Data > 1000, "a real mp3 response was not decoded");
    }

    private static async Task EmotionSwitch()
    {
        var bodies = new List<JsonElement>();
        var handler = new Handler(async (request, ct) =>
        {
            bodies.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone());
            return PcmResponse(0.2);
        });
        using var client = Client(handler);
        foreach (var emotion in new[] { "happy", "shy", "angry" })
            await client.SpeechWavAsync("我回来啦", emotion, default);
        Check(bodies.Select(item => item.GetProperty("voice").GetString()).Distinct().Count() == 1, "voice id drifted with the emotion");
        var instructions = bodies.Select(item => item.GetProperty("instructions").GetString() ?? "").ToArray();
        Check(instructions.Distinct().Count() == 3, "emotions produced the same instruction");
        Check(instructions[0].Contains("笑意", StringComparison.Ordinal) && instructions[1].Contains("害羞", StringComparison.Ordinal) &&
            instructions[2].Contains("抱怨", StringComparison.Ordinal), "instructions do not describe the emotion");
    }

    private static async Task DesignMode()
    {
        string? body = null;
        var handler = new Handler(async (request, ct) =>
        {
            body = await request.Content!.ReadAsStringAsync(ct);
            return PcmResponse(0.2);
        });
        using var client = new BreezeTtsClient("http://127.0.0.1:8080", null, "声音年轻", "pcm", TimeSpan.FromSeconds(20), handler);
        await client.SpeechWavAsync("还没保存音色", "normal", default);
        using var json = JsonDocument.Parse(body!);
        Check(!json.RootElement.TryGetProperty("voice", out _), "a missing voice id was still sent");
        Check(json.RootElement.GetProperty("instructions").GetString()!.Contains("声音年轻", StringComparison.Ordinal), "design lost the description");
    }

    private static async Task BusyRetry()
    {
        var calls = 0;
        var handler = new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"error\":\"busy\"}") }
                : PcmResponse(0.2));
        });
        using var client = Client(handler);
        var wav = await client.SpeechWavAsync("等一下", null, default);
        Check(calls == 2 && WavInfo(wav).Rate == 16000, "a busy server was not retried");
        var always = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)));
        using var busy = Client(always);
        await Throws<BreezeBusyException>(() => busy.SpeechWavAsync("一直忙", null, default));
        Check(always.Calls == 3, "a permanently busy server was not given up after three attempts");
    }

    private static async Task Rejections()
    {
        await Throws<InvalidDataException>(() => Client(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)))).SpeechWavAsync("坏了", null, default));
        await Throws<InvalidDataException>(() => Client(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>nope</html>", Encoding.UTF8, "text/html") })))
            .SpeechWavAsync("不是音频", null, default));
        await Throws<InvalidDataException>(() => Client(new Handler(async (_, _) =>
        {
            var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            message.Content.Headers.ContentType = new("audio/pcm");
            return await Task.FromResult(message);
        })).SpeechWavAsync("空音频", null, default));
        // 超过 60 秒的整条丢掉：QQ 语音装不下。
        await Throws<InvalidDataException>(() => Client(new Handler((_, _) => Task.FromResult(PcmResponse(61))))
            .SpeechWavAsync("太长", null, default));
        var dead = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        using var client = Client(dead);
        await Throws<InvalidDataException>(() => client.SpeechWavAsync("第一次", null, default));
        await Throws<InvalidDataException>(() => client.SpeechWavAsync("第二次", null, default));
        Check(dead.Calls == 1, "a failed service was hammered again instead of being cooled down");
    }

    private static async Task Fallback()
    {
        var dead = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var events = new List<string>();
        using var engine = new SpeechEngine(new VoiceSettings { TtsProvider = "breeze", BreezeVoiceId = "dafeiyu" }, null,
            (status, _) => { lock (events) events.Add(status); }, Local, dead);
        Check(engine.EmotionEnabled, "emotion was silently disabled for breeze");
        var first = await engine.SynthesizeAsync(new VoiceCue("嗯嗯，我知道啦", "happy", 20, true), default);
        var second = await engine.SynthesizeAsync(new VoiceCue("还有一句", null, 20, true), default);
        Check(WavInfo(first).Rate == 16000 && WavInfo(second).Rate == 16000, "fallback did not produce a valid voice");
        Check(dead.Calls == 1, "the dead service was contacted again inside the cooldown");
        Check(events.Contains("breeze_voice_failed") && events.Contains("voice_fallback"), "the fallback was not logged");
    }

    private static async Task FallbackOff()
    {
        var dead = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var localCalls = 0;
        using var engine = new SpeechEngine(new VoiceSettings { TtsProvider = "breeze", BreezeVoiceId = "dafeiyu", TtsFallbackEnabled = false },
            null, null, (_, _) => { localCalls++; return Local("", default); }, dead);
        await Throws<InvalidDataException>(() => engine.SynthesizeAsync(new VoiceCue("只有 breeze", null, 20, true), default));
        Check(localCalls == 0, "edge was used although the fallback is off");
    }

    private static async Task ToneFromState()
    {
        var root = Path.Combine(Path.GetTempPath(), "breeze-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var companions = new CompanionStore(root);
            var instructions = new List<string>();
            var handler = new Handler(async (request, ct) =>
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                lock (instructions) instructions.Add(json.RootElement.GetProperty("instructions").GetString() ?? "");
                return PcmResponse(0.2);
            });
            using var engine = new SpeechEngine(new VoiceSettings { TtsProvider = "breeze", BreezeVoiceId = "dafeiyu" }, companions, null, Local, handler);
            companions.SetMeters(20, 700, 20);
            await engine.SynthesizeAsync(new VoiceCue("我好喜欢你呀", null, 20, true), default);
            companions.SetMeters(20, -700, 10);
            await engine.SynthesizeAsync(new VoiceCue("随便你吧", null, 20, true), default);
            companions.SetMeters(20, 500, 70);
            await engine.SynthesizeAsync(new VoiceCue("又跟她聊这么久", null, 20, true), default);
            companions.SetMeters(20, 300, 10);
            await engine.SynthesizeAsync(new VoiceCue("模型标了就别再猜", "serious", 20, true), default);
            Check(instructions.Count == 4, "not every voice request reached the service");
            Check(instructions[0].Contains("温柔", StringComparison.Ordinal), "high affection did not become a soft tone");
            Check(instructions[1].Contains("抱怨", StringComparison.Ordinal), "low affection did not become a complaining tone");
            Check(instructions[2].Contains("抱怨", StringComparison.Ordinal), "jealousy did not become an angry tone");
            Check(instructions[3].Contains("认真", StringComparison.Ordinal), "the model emotion was overridden by the saved state");
            using var still = new SpeechEngine(new VoiceSettings { TtsProvider = "breeze", BreezeVoiceId = "dafeiyu", BreezeEmotionEnabled = false },
                companions, null, Local, handler);
            Check(!still.EmotionEnabled, "emotion control could not be turned off");
            await still.SynthesizeAsync(new VoiceCue("固定音色朗读", "happy", 20, true), default);
            Check(instructions[4].Contains("自然、亲切", StringComparison.Ordinal), "emotion control off still changed the tone");
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task SettingsAndParsing()
    {
        Reject(() => new VoiceSettings { TtsProvider = "sapi" }.Validate());
        Reject(() => new VoiceSettings { BreezeBaseUrl = "http://127.0.0.1:8080/?x=1" }.Validate());
        Reject(() => new VoiceSettings { BreezeBaseUrl = "ftp://127.0.0.1" }.Validate());
        Reject(() => new VoiceSettings { BreezeVoiceId = "大肥鱼" }.Validate());
        Reject(() => new VoiceSettings { BreezeResponseFormat = "opus" }.Validate());
        Reject(() => new VoiceSettings { BreezeTimeoutSeconds = 2 }.Validate());
        Reject(() => new VoiceSettings { BreezeInstructions = new string('x', 2001) }.Validate());
        var path = Path.Combine(Path.GetTempPath(), "voice-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"ttsProvider\":\"BREEZE\",\"breezeBaseUrl\":\" http://127.0.0.1:8080 \",\"breezeVoiceId\":\" dafeiyu \",\"breezeResponseFormat\":\"PCM\"}");
            var settings = VoiceSettings.Load(path);
            Check(settings.TtsProvider == "breeze" && settings.BreezeBaseUrl == "http://127.0.0.1:8080" &&
                settings.BreezeVoiceId == "dafeiyu" && settings.BreezeResponseFormat == "pcm", "settings were not normalized");
            var group = new HashSet<string>(StringComparer.Ordinal);
            var reply = GroupChatBrain.Parse("{\"send\":true,\"text\":\"嗯哼\",\"voice\":true,\"emotion\":\"SHY\"}", group);
            Check(reply.Voice && reply.Emotion == "shy" && reply.Text == "嗯哼", "emotion was not accepted and normalized");
            Check(GroupChatBrain.Parse("{\"send\":true,\"text\":\"嗯哼\",\"voice\":true,\"emotion\":\"rage\"}", group).Emotion is null, "an unknown emotion was kept");
            Check(GroupChatBrain.Parse("{\"send\":true,\"text\":\"嗯哼\",\"voice\":true,\"emotion\":7}", group).Emotion is null, "a non-string emotion was kept");
            Check(GroupChatBrain.Parse("{\"send\":true,\"text\":\"嗯哼\",\"voice\":false,\"emotion\":\"happy\"}", group).Emotion is null,
                "an emotion was kept for a text only reply");
            ThrowsJson(() => GroupChatBrain.Parse("{\"send\":true,\"text\":\"嗯哼\",\"emotion\":\"happy\",\"tone\":1}", group));
            var prompt = GroupChatBrain.SystemPrompt([], GroupChatMode.Mention, voiceEnabled: true, voiceEmotion: true);
            Check(prompt.Contains("\"emotion\"", StringComparison.Ordinal) && prompt.Contains("serious", StringComparison.Ordinal) &&
                prompt.Contains("绝不发给对方", StringComparison.Ordinal), "the emotion field is not documented to the model");
            Check(!GroupChatBrain.SystemPrompt([], GroupChatMode.Mention, voiceEnabled: true).Contains("emotion", StringComparison.Ordinal),
                "emotion was offered while it is off");
            Check(GroupChatBrain.SystemPrompt([], GroupChatMode.Mention).Contains("也不要给emotion", StringComparison.Ordinal),
                "a muted voice still offers an emotion");
        }
        finally { File.Delete(path); }
        return Task.CompletedTask;
    }

    private static async Task VoiceOverride()
    {
        Check(GroupChatBrain.ExplicitVoiceRequest("你去发个语音试试"), "an explicit voice request was not recognised");
        Check(GroupChatBrain.ExplicitVoiceRequest("小鱼给我唱雨爱听听"), "a singing request was not recognised");
        Check(!GroupChatBrain.ExplicitVoiceRequest("已经装好了"), "an ordinary message was treated as a voice request");
        Check(!GroupChatBrain.ExplicitVoiceRequest("不要发语音，打字就行"), "a refusal was treated as a voice request");
        const string json = "{\"send\":true,\"text\":\"窗外的雨滴 一滴滴累积\",\"voice\":false}";
        var message = new ParsedGroupMessage(0, 20, 1, DateTimeOffset.UtcNow, "你去发个语音试试", false, false, IsPrivate: true);
        var forced = new GroupChatBrain(_ => new FixedReply(json), [], 99, voiceEnabled: true);
        Check((await forced.GenerateAsync([message], default, message, GroupChatTrigger.Private)).Voice,
            "an explicit voice request was still answered with text");
        var muted = new GroupChatBrain(_ => new FixedReply(json), [], 99);
        Check(!(await muted.GenerateAsync([message], default, message, GroupChatTrigger.Private)).Voice,
            "voice was forced although the engine is off");
        var chat = message with { Text = "今天好累" };
        var ordinary = new GroupChatBrain(_ => new FixedReply(json), [], 99, voiceEnabled: true);
        Check(!(await ordinary.GenerateAsync([chat], default, chat, GroupChatTrigger.Private)).Voice,
            "an ordinary message forced voice");
    }

    private static async Task ResponderFlow()
    {
        var directory = Path.Combine(Path.GetTempPath(), "breeze-responder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Files/Memes"));
        try
        {
            var sent = new List<JsonElement>();
            var cue = default(VoiceCue);
            var handler = new Handler(async (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath == "/get_login_info")
                    return Json("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"user_id\":99}}");
                var body = await request.Content!.ReadAsStringAsync(ct);
                lock (sent) sent.Add(JsonDocument.Parse(body).RootElement.Clone());
                return Json("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":7}}");
            });
            var policy = new GroupChatPolicy(new() { AllowedGroupIds = [10], SelfId = 99, BotNames = ["机器人"] });
            using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10"], handler);
            var brain = new GroupChatBrain(_ => new FixedReply("{\"send\":true,\"text\":\"我回来啦\",\"voice\":true,\"emotion\":\"excited\"}"), []);
            await using (var responder = new GroupChatResponder(policy, brain, new MemeLibrary(directory), client, true, 99,
                synthesizeVoice: (value, _) => { cue = value; return Local("", default); }))
            {
                var outcome = await responder.HandleAsync(policy.Decide(Message(1)), default);
                Check(outcome.Status == "api_accepted" && outcome.Voice, "the voice reply was not confirmed");
            }
            Check(sent.Count == 1, "the voice reply also sent the text");
            Check(sent[0].GetProperty("message")[0].GetProperty("type").GetString() == "record", "voice was not sent as a record");
            Check(cue is not null && cue.Emotion == "excited" && cue.UserId == 20 && !cue.Private, "the cue lost the emotion or the sender");
            var failing = new GroupChatBrain(_ => new FixedReply("{\"send\":true,\"text\":\"第二句\",\"voice\":true}"), []);
            using var second = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10"], handler);
            var secondPolicy = Policy();
            await using (var responder = new GroupChatResponder(secondPolicy, failing, new MemeLibrary(directory), second, true, 99,
                synthesizeVoice: (_, _) => throw new InvalidDataException("没有声音")))
            {
                var outcome = await responder.HandleAsync(secondPolicy.Decide(Message(2)), default);
                Check(outcome.Status == "api_accepted" && outcome.Text == "第二句" && !outcome.Voice, "a voice failure dropped the text");
            }
            Check(sent.Count == 2 && sent[1].GetProperty("message")[0].GetProperty("type").GetString() == "text",
                "a failed voice did not fall back to the text message");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task VoiceOnlyReply()
    {
        var directory = Path.Combine(Path.GetTempPath(), "breeze-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Files/Memes"));
        try
        {
            var sent = new List<string>();
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new Handler(async (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath == "/get_login_info")
                    return Json("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"user_id\":99}}");
                var body = await request.Content!.ReadAsStringAsync(ct);
                var type = JsonDocument.Parse(body).RootElement.GetProperty("message")[0].GetProperty("type").GetString() ?? "";
                lock (sent) sent.Add(type);
                return Json("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":7}}");
            });
            var policy = Policy();
            using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10"], handler);
            var brain = new GroupChatBrain(_ => new FixedReply("{\"send\":true,\"text\":\"只说声音\",\"voice\":true}"), []);
            await using var responder = new GroupChatResponder(policy, brain, new MemeLibrary(directory), client, true, 99,
                synthesizeVoice: async (_, ct) => { await released.Task.WaitAsync(ct); return await Local("", ct); });
            var work = responder.HandleAsync(policy.Decide(Message(1)), default);
            await Task.Delay(400);
            Check(sent.Count == 0, "text was sent before the voice was ready");
            released.SetResult();
            var outcome = await work.WaitAsync(TimeSpan.FromSeconds(5));
            Check(outcome.Status == "api_accepted" && outcome.Voice, "the voice reply was not confirmed");
            Check(sent.SequenceEqual(["record"]), "the reply did not send exactly one record");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task LoopbackRequest()
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try { await LoopbackOnce(); return; }
            catch (HttpListenerException error) { last = error; await Task.Delay(250); }
        }
        throw last!;
    }

    private static async Task LoopbackOnce()
    {
        var port = Port();
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        listener.Start();
        var payload = Pcm(24000, 0.2);
        var serving = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            try
            {
                Check(context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/v1/audio/speech", "wrong loopback request");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "audio/pcm";
                context.Response.Headers["X-Sample-Rate"] = "24000";
                await context.Response.OutputStream.WriteAsync(payload);
            }
            finally { context.Response.Close(); }
        });
        using var client = new BreezeTtsClient("http://127.0.0.1:" + port, "dafeiyu", "声音年轻", "pcm", TimeSpan.FromSeconds(20));
        var wav = await client.SpeechWavAsync("真的走一遍网络", "happy", default);
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        var info = WavInfo(wav);
        Check(info.Rate == 16000 && info.Data > 1000, "loopback audio was not converted");
    }

    private static BreezeTtsClient Client(Handler handler) =>
        new("http://127.0.0.1:8080", "dafeiyu", "声音年轻", "pcm", TimeSpan.FromSeconds(20), handler);

    private static async Task ArchiveVoice()
    {
        var root = Path.Combine(Path.GetTempPath(), "breeze-archive-" + Guid.NewGuid().ToString("N"));
        var saved = new List<JsonElement>();
        using var engine = new SpeechEngine(new VoiceSettings { TtsProvider = "breeze", BreezeVoiceId = "dafeiyu" }, null,
            (status, details) =>
            {
                if (status != "voice_saved") return;
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(details));
                saved.Add(json.RootElement.Clone());
            }, Local, new Handler((_, _) => Task.FromResult(PcmResponse(0.2))), root);
        try
        {
            var wav = await engine.SynthesizeAsync(new VoiceCue("语音留档测试", "happy", 20, true), default);
            var files = Directory.GetFiles(root, "*.wav");
            Check(files.Length == 1, "the sent voice was not archived");
            Check(File.ReadAllBytes(files[0]).SequenceEqual(wav), "the archived file is not the voice that was sent");
            Check(Path.GetFileName(files[0]).Contains("-breeze-happy-6.wav", StringComparison.Ordinal),
                "the archive name lost the engine, emotion or length");
            Check(saved.Count == 1 && saved[0].GetProperty("text").GetString() == "语音留档测试",
                "the archived voice lost its script");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static Task ArchiveCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "breeze-clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var old = Path.Combine(root, "20261001-000000-000-breeze-normal-10.wav");
            var fresh = Path.Combine(root, "20261028-000000-000-breeze-normal-10.wav");
            var other = Path.Combine(root, "keep.txt");
            File.WriteAllBytes(old, new byte[8]);
            File.WriteAllBytes(fresh, new byte[8]);
            File.WriteAllText(other, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
            File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddDays(-6));
            var removed = GroupChatRuntime.PurgeVoiceArchive(root, DateTimeOffset.UtcNow);
            Check(removed == 1 && !File.Exists(old) && File.Exists(fresh) && File.Exists(other),
                "the retention pass deleted the wrong files");
            Check(GroupChatRuntime.PurgeVoiceArchive(Path.Combine(root, "missing"), DateTimeOffset.UtcNow) == 0,
                "a missing archive directory was not treated as empty");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        return Task.CompletedTask;
    }

    private static Task<byte[]> Local(string text, CancellationToken ct) => Task.FromResult(QqVoice.Wav(Pcm(16000, 0.1), 16000));

    private static byte[] Pcm(int rate, double seconds)
    {
        var samples = (int)(rate * seconds);
        var bytes = new byte[samples * 2];
        for (var index = 0; index < samples; index++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2),
                (short)(short.MaxValue * 0.3 * Math.Sin(2 * Math.PI * 220 * index / rate)));
        return bytes;
    }

    private static HttpResponseMessage PcmResponse(double seconds) => AudioResponse(Pcm(24000, seconds), "audio/pcm", 24000);

    private static HttpResponseMessage AudioResponse(byte[] body, string media, int rate)
    {
        var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        message.Content.Headers.ContentType = new(media);
        message.Headers.Add("X-Sample-Rate", rate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return message;
    }

    private static (int Rate, int Channels, int Bits, int Data) WavInfo(byte[] wav)
    {
        Check(wav.Length >= 44 && wav[0] == 'R' && wav[1] == 'I' && wav[8] == 'W' && wav[9] == 'A', "not a wav");
        return (BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)), BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)),
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)), BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
    }

    private static GroupChatPolicy Policy() => new(new() { AllowedGroupIds = [10], SelfId = 99, BotNames = ["机器人"] });
    private static ParsedGroupMessage Message(long id) => new(10, 20, id, DateTimeOffset.UtcNow, "机器人你好", true, false);
    private static int Port() { var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); tcp.Start(); var value = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop(); return value; }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Reject(Action operation) { try { operation(); } catch (ArgumentException) { return; } throw new Exception("invalid settings accepted"); }
    private static void ThrowsJson(Action operation)
    {
        try { operation(); } catch (Exception error) when (error is InvalidDataException or ArgumentException) { return; }
        throw new Exception("invalid reply accepted");
    }
    private static async Task Throws<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); } catch (T) { return; } catch (Exception error) { throw new Exception("wrong failure: " + error.GetType().Name); }
        throw new Exception("failure was accepted");
    }

    private sealed class FixedReply(string output) : IStreamingReply
    {
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); yield return output; }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return action(request, ct); }
    }
}
