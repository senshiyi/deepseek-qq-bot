using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AudioBridge;

public static class GroupChatFlowChecks
{
    public static async Task<int> RunAsync()
    {
        var tests = new (string, Func<Task>)[]
        {
            ("strict model decisions cannot choose files, groups or unknown memes", Parse),
            ("current trigger remains separate from chronological context", CurrentTrigger),
            ("pause abandons an uncooperative model and fences late output", PauseLateReply),
            ("preview sends nothing; successful send alone enters own context", PreviewAndHistory),
            ("curated native face choices encode one QQ group or private message", NativeFaceResponse),
            ("confirmed same-session memes are suppressed for 120 seconds without losing text or crossing sessions", RecentMemeSessions),
            ("explicit repeated meme requests retain policy limits and send the requested asset", ExplicitRepeatedMeme),
            ("multiple messages only when the model asks for lines", LineMessages),
            ("preview, failed sends and cancelled generations do not reserve a meme choice", UnsentMemeChoices),
            ("native faces absent from the registered library are rejected before HTTP", UnknownNativeFace),
            ("account change after generation pauses without a group send", AccountSwitch),
            ("independent budget instances reload the shared ledger", SharedBudget),
            ("an ambient group reply quotes its trigger and direct replies do not", AmbientQuote),
            ("a group reply uses the triggering contact's own persona only", GroupPersona),
            ("real loopback HMAC event reaches simulated OneBot once", HttpEndToEnd),
            ("runtime evaluates a pending ordinary message after cooldown without another event", PendingAmbientRuntime),
            ("chat duration rejects invalid values before starting", DurationValidation),
            ("continuous chat has no deadline and external cancellation releases listener and session", ContinuousRuntime),
            ("bounded chat schedules its unchanged deadline and releases resources", BoundedRuntime)
        };
        foreach (var (name, run) in tests) { await run(); Console.WriteLine("PASS messaging flow: " + name); }
        return tests.Length;
    }

    private static Task Parse()
    {
        var ids = new HashSet<string> { "roger" };
        Check(GroupChatBrain.Parse("{\"send\":true,\"text\":\"收到\",\"memeId\":\"roger\"}", ids).MemeId == "roger", "valid decision rejected");
        foreach (var invalid in new[] { "not JSON", "{\"send\":true,\"text\":\"x\",\"group_id\":999}",
            "{\"send\":true,\"text\":\"x\",\"memeId\":\"../../secret\"}", "{\"send\":1,\"text\":\"x\"}",
            "{\"send\":true,\"text\":\"x\",\"text\":\"duplicate\"}" })
        {
            var rejected = false;
            try { GroupChatBrain.Parse(invalid, ids); } catch (Exception e) when (e is JsonException or InvalidDataException) { rejected = true; }
            Check(rejected, "untrusted decision accepted");
        }
        return Task.CompletedTask;
    }

    private static async Task CurrentTrigger()
    {
        var actual = "";
        var brain = new GroupChatBrain(question => { actual = question; return new FixedReply(Json); }, ["roger"], 99);
        var trigger = Message(1, "当前问题");
        await brain.GenerateAsync([trigger, Message(2, "更晚到的图片闲聊")], default, trigger);
        Check(actual == "当前问题", "chronological context replaced trigger/image question");
    }

    private static async Task PauseLateReply()
    {
        var directory = MakeLibrary();
        try
        {
            var started = Ready(); var release = Ready();
            var handler = new FakeApi();
            using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10"], handler);
            var now = DateTimeOffset.UtcNow;
            var policy = new GroupChatPolicy(new() { AllowedGroupIds = [10], SelfId = 99, BotNames = ["机器人"] }, () => now);
            var library = new MemeLibrary(directory); library.AddFace(14, ["微笑"]);
            var requests = 0;
            var brain = new GroupChatBrain(_ => Interlocked.Increment(ref requests) == 1
                ? new SlowReply(started, release, "{\"send\":true,\"text\":\"旧回复\",\"memeId\":\"qq_face_14\"}")
                : new FixedReply(Json), ["roger", "qq_face_14"]);
            await using var responder = new GroupChatResponder(policy, brain, library, client, true, 99);
            var work = responder.HandleAsync(policy.Decide(Message(1)), default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await responder.SetPausedAsync(true);
            try { await work.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("pause reported normal reply"); }
            catch (OperationCanceledException) { }
            Check(handler.Calls == 0, "cancelled native face generation sent HTTP");
            await responder.SetPausedAsync(false);
            now = now.AddSeconds(9);
            var current = await responder.HandleAsync(policy.Decide(Message(2)), default);
            Check(current.Status == "api_accepted", "new generation did not recover after native face cancellation");
            release.TrySetResult();
            await Task.Delay(25);
            using var sent = JsonDocument.Parse(handler.LastSendBody!);
            Check(handler.Calls == 2 && handler.Sends == 1 && !policy.Snapshot().Groups.Single().Busy &&
                sent.RootElement.GetProperty("message")[1].GetProperty("type").GetString() == "image", "late native face sent into the new generation or busy leaked");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task PreviewAndHistory()
    {
        var directory = MakeLibrary();
        try
        {
            foreach (var live in new[] { false, true })
            {
                var handler = new FakeApi();
                using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10"], handler);
                var policy = Policy();
                await using var responder = new GroupChatResponder(policy,
                    new GroupChatBrain(_ => new FixedReply(Json), ["roger"]), new MemeLibrary(directory), client, live, 99);
                var outcome = await responder.HandleAsync(policy.Decide(Message(1)), default);
                Check(handler.Calls == (live ? 2 : 0), "preview performed HTTP or login/send failed");
                Check(policy.Snapshot().Groups.Single().ContextCount == (live ? 2 : 1), "preview/confirmed own history boundary wrong");
                Check(outcome.Status == (live ? "api_accepted" : "preview"), "wrong outcome");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task AccountSwitch()
    {
        var directory = MakeLibrary();
        try
        {
            var handler = new FakeApi { Account = 88 };
            using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10"], handler);
            var policy = Policy();
            await using var responder = new GroupChatResponder(policy, new GroupChatBrain(_ => new FixedReply(Json), ["roger"]), new MemeLibrary(directory), client, true, 99);
            var outcome = await responder.HandleAsync(policy.Decide(Message(1)), default);
            Check(outcome.Status == "account_changed_paused" && policy.Snapshot().Paused && handler.Sends == 0, "changed account sent a reply");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task NativeFaceResponse()
    {
        var directory = MakeLibrary();
        try
        {
            var library = new MemeLibrary(directory); library.AddFace(14, ["微笑"]);
            foreach (var isPrivate in new[] { false, true })
                foreach (var text in new[] { "", "收到。" })
                {
                    var handler = new FakeApi();
                    using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10"], handler, ["20"]);
                    var policy = new GroupChatPolicy(new() { AllowedGroupIds = [10], AllowedPrivateUserIds = [20], SelfId = 99, BotNames = ["机器人"] });
                    var reply = JsonSerializer.Serialize(new { send = true, text, memeId = "qq_face_14" });
                    var brain = new GroupChatBrain(_ => new FixedReply(reply), library.List().Select(x => x.Id));
                    await using var responder = new GroupChatResponder(policy, brain, library, client, true, 99);
                    var outcome = await responder.HandleAsync(policy.Decide(Message(1) with { GroupId = isPrivate ? 0 : 10, IsPrivate = isPrivate }), default);
                    Check(outcome.Status == "api_accepted" && handler.LastSendBody is not null, "native face response did not reach the authorized API");
                    using var sent = JsonDocument.Parse(handler.LastSendBody!);
                    var root = sent.RootElement;
                    var segments = root.GetProperty("message");
                    var face = segments[text.Length == 0 ? 0 : 1];
                    Check(outcome.Status == "api_accepted" && handler.Calls == 2 && handler.Sends == 1 &&
                        root.GetProperty(isPrivate ? "user_id" : "group_id").GetString() == (isPrivate ? "20" : "10") &&
                        segments.GetArrayLength() == (text.Length == 0 ? 1 : 2) && face.GetProperty("type").GetString() == "face" &&
                        face.GetProperty("data").GetProperty("id").GetString() == "14",
                        "native face was not encoded inside exactly one authorized message");
                    if (text.Length > 0) Check(segments[0].GetProperty("type").GetString() == "text" &&
                        segments[0].GetProperty("data").GetProperty("text").GetString() == text, "native face reply lost its text");
                }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task RecentMemeSessions()
    {
        var directory = MakeLibrary();
        try
        {
            var library = new MemeLibrary(directory); library.AddFace(14, ["惊讶"]);
            var handler = new FakeApi();
            using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10", "20"], handler, ["20", "21"]);
            var now = DateTimeOffset.UtcNow;
            var policy = new GroupChatPolicy(new() { AllowedGroupIds = [10, 20], AllowedPrivateUserIds = [20, 21], SelfId = 99,
                BotNames = ["机器人"], MentionInterval = TimeSpan.FromSeconds(3), PrivateInterval = TimeSpan.FromSeconds(3) }, () => now);
            var response = Json;
            var brain = new GroupChatBrain(_ => new FixedReply(response), library.List().Select(x => x.Id));
            await using var responder = new GroupChatResponder(policy, brain, library, client, true, 99, clock: () => now);
            var nextId = 0L;
            async Task<GroupChatOutcome> Send(long target, bool isPrivate, string text, string? meme)
            {
                now = now.AddSeconds(4);
                response = JsonSerializer.Serialize(new { send = true, text, memeId = meme });
                var message = new ParsedGroupMessage(isPrivate ? 0 : target, isPrivate ? target : 20, ++nextId, now,
                    "机器人测试表情", !isPrivate, false, isPrivate);
                var decision = policy.Decide(message);
                Check(decision.ShouldReply, "fixture did not reserve its message");
                return await responder.HandleAsync(decision, default);
            }
            var first = await Send(10, false, "收到", "roger");
            Check(first.Status == "api_accepted" && first.MemeId == "roger", "first confirmed meme was not sent");
            var textKept = await Send(10, false, "继续回答", "roger");
            using (var sent = JsonDocument.Parse(handler.LastSendBody!))
                Check(textKept.Status == "api_accepted" && textKept.MemeId is null && textKept.Text == "继续回答" &&
                    sent.RootElement.GetProperty("message").GetArrayLength() == 1, "repeated meme removed text or remained in its HTTP payload");
            var calls = handler.Calls;
            var memeOnly = await Send(10, false, "", "roger");
            Check(memeOnly.Status == "repeated_meme_suppressed" && handler.Calls == calls, "empty repeated meme performed HTTP or was misreported as sent");
            await Send(10, false, "纯文字不重置短期表情状态", null);
            calls = handler.Calls;
            Check((await Send(10, false, "", "roger")).Status == "repeated_meme_suppressed" && handler.Calls == calls,
                "text-only success reset the repeat window");
            Check((await Send(20, true, "", "roger")).MemeId == "roger" &&
                (await Send(21, true, "", "roger")).MemeId == "roger" &&
                (await Send(20, false, "", "roger")).MemeId == "roger", "meme state crossed private contacts or group/private scope");
            Check((await Send(10, false, "", "qq_face_14")).MemeId == "qq_face_14" &&
                (await Send(10, false, "", "roger")).MemeId == "roger", "a different confirmed meme did not replace the latest choice");
            now = now.AddSeconds(116); // Send advances four seconds: exactly 120 since the confirmed roger.
            Check((await Send(10, false, "", "roger")).MemeId == "roger", "the same meme remained blocked at the 120-second boundary");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task LineMessages()
    {
        var ids = new HashSet<string>();
        var split = GroupChatBrain.Parse("{\"send\":true,\"lines\":[\"第一句\",\"第二句\"],\"memeId\":null}", ids);
        Check(split.Messages is { Length: 2 } lines && lines[1] == "第二句" && split.Text == "第一句 第二句", "lines array did not become separate messages");
        foreach (var invalid in new[] { "{\"send\":true,\"lines\":[\"一\"],\"text\":\"二\"}", "{\"send\":true,\"lines\":[\"一\",\"二\",\"三\",\"四\"]}",
            "{\"send\":true,\"lines\":[\"一\",\" \"]}", "{\"send\":true,\"lines\":[1]}", "{\"send\":true,\"lines\":\"一\"}" })
        {
            var rejected = false;
            try { GroupChatBrain.Parse(invalid, ids); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "invalid lines decision accepted");
        }
        var directory = MakeLibrary();
        try
        {
            foreach (var (response, expected) in new[]
            {
                ("{\"send\":true,\"text\":\"第一句\\n第二句\",\"memeId\":null}", 1),
                ("{\"send\":true,\"lines\":[\"第一句\",\"第二句\"],\"memeId\":null}", 2)
            })
            {
                var handler = new FakeApi();
                using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10"], handler);
                var policy = Policy();
                var brain = new GroupChatBrain(_ => new FixedReply(response), ["roger"]);
                await using var responder = new GroupChatResponder(policy, brain, new MemeLibrary(directory), client, true, 99);
                var outcome = await responder.HandleAsync(policy.Decide(Message(1)), default);
                Check(outcome.Status == "api_accepted" && handler.Sends == expected && handler.Calls == expected + 1,
                    "message count differs from the model decision");
                if (expected == 2)
                {
                    using var sent = JsonDocument.Parse(handler.LastSendBody!);
                    Check(sent.RootElement.GetProperty("message")[0].GetProperty("data").GetProperty("text").GetString() == "第二句",
                        "the second message did not carry only the second line");
                }
                Check(policy.Snapshot().Groups.Single().ContextCount == 2, "multi-message reply entered own history more than once");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task ExplicitRepeatedMeme()
    {
        var directory = MakeLibrary();
        try
        {
            var library = new MemeLibrary(directory);
            var handler = new FakeApi();
            using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10"], handler, ["20"]);
            var now = DateTimeOffset.UtcNow;
            var policy = new GroupChatPolicy(new() { AllowedGroupIds = [10], AllowedPrivateUserIds = [20], SelfId = 99,
                BotNames = ["机器人"], PrivateInterval = TimeSpan.FromSeconds(3) }, () => now);
            var modelCalls = 0;
            var brain = new GroupChatBrain(library.List, (_, _, _) => { modelCalls++; return new FixedReply(Json); },
                persona: new PersonaSettings { MemeProbabilityPercent = 1 });
            await using var responder = new GroupChatResponder(policy, brain, library, client, true, 99, clock: () => now);
            for (var index = 1; index <= 2; index++)
            {
                var message = new ParsedGroupMessage(0, 20, index, now, "给我发roger表情包", false, false, true);
                var decision = policy.Decide(message);
                Check(decision.ShouldReply, "explicit fixture did not reserve its message");
                var outcome = await responder.HandleAsync(decision, default);
                Check(outcome.Status == "api_accepted" && outcome.MemeId == "roger", "explicit request lost its selected or repeated image");
                now = now.AddSeconds(4);
            }
            Check(handler.Sends == 2 && modelCalls == 0, "explicit ID request invoked model or failed to send exactly once");
            now = now.AddSeconds(-4);
            Check(!policy.Decide(new(0, 20, 3, now, "给我发roger表情包", false, false, true)).ShouldReply,
                "explicit meme bypassed normal per-contact cooldown");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task UnsentMemeChoices()
    {
        var directory = MakeLibrary();
        try
        {
            foreach (var live in new[] { false, true })
            {
                var handler = new FakeApi { FailNextSend = live };
                using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10"], handler);
                var now = DateTimeOffset.UtcNow;
                var policy = new GroupChatPolicy(new() { AllowedGroupIds = [10], SelfId = 99, BotNames = ["机器人"],
                    MentionInterval = TimeSpan.FromSeconds(3) }, () => now);
                await using var responder = new GroupChatResponder(policy, new GroupChatBrain(_ => new FixedReply(
                    "{\"send\":true,\"text\":\"\",\"memeId\":\"roger\"}"), ["roger"]), new MemeLibrary(directory), client, live, 99, clock: () => now);
                for (var index = 1; index <= 2; index++)
                {
                    now = now.AddSeconds(4);
                    var decision = policy.Decide(Message(index) with { At = now });
                    Check(decision.ShouldReply, "unsent fixture did not reserve");
                    if (live && index == 1)
                    {
                        try { await responder.HandleAsync(decision, default); throw new Exception("failed send reported success"); }
                        catch (OneBotMessageException) { }
                    }
                    else
                    {
                        var result = await responder.HandleAsync(decision, default);
                        Check(result.Status == (live ? "api_accepted" : "preview") && result.MemeId == "roger",
                            "preview or failed send polluted the successful-meme state");
                    }
                }
                Check(handler.Sends == (live ? 2 : 0), "preview sent a meme or failed send was silently retried");
            }
            var started = Ready(); var release = Ready(); var calls = 0;
            var cancelledHandler = new FakeApi();
            using var cancelledClient = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10"], cancelledHandler);
            var cancelledNow = DateTimeOffset.UtcNow;
            var cancelledPolicy = new GroupChatPolicy(new() { AllowedGroupIds = [10], SelfId = 99, BotNames = ["机器人"],
                MentionInterval = TimeSpan.FromSeconds(3) }, () => cancelledNow);
            const string memeOnly = "{\"send\":true,\"text\":\"\",\"memeId\":\"roger\"}";
            var cancelledBrain = new GroupChatBrain(_ => Interlocked.Increment(ref calls) == 1
                ? new SlowReply(started, release, memeOnly) : new FixedReply(memeOnly), ["roger"]);
            await using var cancelledResponder = new GroupChatResponder(cancelledPolicy, cancelledBrain, new MemeLibrary(directory), cancelledClient,
                true, 99, clock: () => cancelledNow);
            var pending = cancelledResponder.HandleAsync(cancelledPolicy.Decide(Message(1) with { At = cancelledNow }), default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await cancelledResponder.SetPausedAsync(true);
            try { await pending; throw new Exception("cancelled generation returned a decision"); } catch (OperationCanceledException) { }
            await cancelledResponder.SetPausedAsync(false);
            cancelledNow = cancelledNow.AddSeconds(4);
            var accepted = await cancelledResponder.HandleAsync(cancelledPolicy.Decide(Message(2) with { At = cancelledNow }), default);
            release.TrySetResult();
            await Task.Delay(25);
            Check(accepted.Status == "api_accepted" && accepted.MemeId == "roger" && cancelledHandler.Sends == 1,
                "cancelled meme choice blocked its next generation or late output sent again");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task UnknownNativeFace()
    {
        var directory = MakeLibrary();
        try
        {
            var handler = new FakeApi();
            using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10"], handler);
            var policy = Policy();
            // Even an obsolete model catalogue must not bypass the current curated manifest.
            var brain = new GroupChatBrain(_ => new FixedReply("{\"send\":true,\"text\":\"\",\"memeId\":\"qq_face_14\"}"), ["qq_face_14"]);
            await using var responder = new GroupChatResponder(policy, brain, new MemeLibrary(directory), client, true, 99);
            var rejected = false;
            try { await responder.HandleAsync(policy.Decide(Message(1)), default); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected && handler.Calls == 0 && !policy.Snapshot().Groups.Single().Busy,
                "unregistered native face reached HTTP or retained its reservation");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static Task SharedBudget()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qq-budget-shared-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "ledger.json");
            var first = new ApiBudget(path, .3, 1);
            var stale = new ApiBudget(path, .3, 1);
            first.Reserve([new("user", "测试")]);
            var refused = false;
            try { stale.Reserve([new("user", "不能覆盖上个实例")]); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "stale instance overwrote budget");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    private static async Task GroupPersona()
    {
        var global = new PersonaSettings { Name = "大肥鱼", Personality = "全局性格", UserAddress = "主人",
            SpeakingStyle = "自然中文", ReplyMaxCharacters = 3000, MemeProbabilityPercent = 100 };
        var personal = global with { Name = "小米", Personality = "只属于21的性格", UserAddress = "同学", ReplyMaxCharacters = 40 };
        var seen = new List<PersonaSettings?>();
        var brain = new GroupChatBrain(() => [new MemeEntry("qq_face_0", "", ["微笑"], 0)],
            (_, _, speakerPersona) => { seen.Add(speakerPersona); return new FixedReply("{\"send\":true,\"text\":\"" + new string('话', 80) + "\"}"); },
            persona: global, personaFor: sender => sender == 21 ? personal : global);
        var fromOther = new ParsedGroupMessage(10, 21, 1, DateTimeOffset.UtcNow, "你好呀", false, false);
        var theirs = await brain.GenerateAsync([fromOther], default, fromOther);
        Check(seen.Count == 1 && seen[0]?.Name == "小米", "the group reply did not use the triggering contact's own persona");
        Check(theirs.Text.Length == 40, "the contact's own reply length was not applied");
        var fromStranger = new ParsedGroupMessage(10, 33, 2, DateTimeOffset.UtcNow, "你好呀", false, false);
        await brain.GenerateAsync([fromStranger], default, fromStranger);
        Check(seen[^1]?.Name == "大肥鱼", "a stranger's group message lost the global persona");
    }

    private static async Task AmbientQuote()
    {
        var directory = MakeLibrary();
        try
        {
            var handler = new FakeApi();
            using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["10"], handler);
            var now = DateTimeOffset.UtcNow;
            var policy = new GroupChatPolicy(new()
            {
                AllowedGroupIds = [10], SelfId = 99, BotNames = ["机器人"], Mode = GroupChatMode.Active,
                ActiveMessageEvery = 1, ActiveInterval = TimeSpan.FromSeconds(3), MentionInterval = TimeSpan.FromSeconds(3)
            }, () => now);
            await using var responder = new GroupChatResponder(policy, new GroupChatBrain(_ => new FixedReply(Json), ["roger"]),
                new MemeLibrary(directory), client, true, 99);
            var ambient = Message(7, "今天下雨了") with { Mentioned = false };
            var decision = policy.Decide(ambient);
            Check(decision.Trigger == GroupChatTrigger.Ambient, "fixture did not produce an ambient trigger: " + decision.Reason);
            Check((await responder.HandleAsync(decision, default)).Status == "api_accepted", "ambient reply was not sent");
            Check(handler.LastSendBody?.Contains("\"reply\"", StringComparison.Ordinal) == true &&
                handler.LastSendBody.Contains("\"id\":\"7\"", StringComparison.Ordinal),
                "an ambient group reply did not quote the message that triggered it");
            now = now.AddSeconds(5);
            var direct = policy.Decide(Message(8, "机器人你好"));
            Check(direct.Trigger == GroupChatTrigger.Mention, "fixture did not produce a direct trigger");
            Check((await responder.HandleAsync(direct, default)).Status == "api_accepted", "direct reply was not sent");
            Check(handler.LastSendBody?.Contains("\"reply\"", StringComparison.Ordinal) == false,
                "a direct @ reply was needlessly quoting");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task HttpEndToEnd()
    {
        var directory = MakeLibrary();
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var apiPort = Port(); var eventPort = Port();
        while (eventPort == apiPort) eventPort = Port();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using var api = new HttpListener();
        api.Prefixes.Add($"http://127.0.0.1:{apiPort}/"); api.Start();
        using var closeApi = stopping.Token.Register(api.Close);
        var observed = Ready();
        var outgoingCalls = 0;
        var apiTask = Task.Run(async () =>
        {
            var login = await api.GetContextAsync();
            Check(login.Request.Url!.AbsolutePath == "/get_login_info", "missing final account recheck");
            var identity = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"user_id\":99}}");
            await login.Response.OutputStream.WriteAsync(identity); login.Response.Close();
            var request = await api.GetContextAsync();
            Check(request.Request.Url!.AbsolutePath == "/send_group_msg", "wrong API endpoint");
            Check(request.Request.Headers["Authorization"] == "Bearer " + token, "missing outbound auth");
            using var json = await JsonDocument.ParseAsync(request.Request.InputStream);
            Check(json.RootElement.GetProperty("group_id").ToString() == "10", "cross-group send");
            Check(json.RootElement.GetProperty("message").GetArrayLength() == 2, "missing text or meme segment");
            Interlocked.Increment(ref outgoingCalls);
            var bytes = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":901}}");
            await request.Response.OutputStream.WriteAsync(bytes);
            request.Response.Close(); observed.TrySetResult();
        });
        try
        {
            using var client = new OneBotMessageClient($"http://127.0.0.1:{apiPort}/", token, ["10"]);
            var policy = Policy();
            await using var responder = new GroupChatResponder(policy,
                new GroupChatBrain(_ => new FixedReply(Json), ["roger"]), new MemeLibrary(directory), client, true, 99);
            Task<GroupChatOutcome>? response = null;
            var receiver = new OneBotEventServer(eventPort, token, root =>
            {
                if (!policy.TryParse(root, out var parsed, out _)) return false;
                var decision = policy.Decide(parsed!);
                if (decision.ShouldReply) response = responder.HandleAsync(decision, stopping.Token);
                return true;
            });
            var serving = receiver.RunAsync(stopping.Token);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var body = JsonSerializer.SerializeToUtf8Bytes(new { post_type = "message", message_type = "group", self_id = 99,
                group_id = 10, user_id = 20, sender = new { user_id = 20 }, message_id = 42,
                time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), message = new[] { new { type = "text", data = new { text = "机器人你好" } } } });
            async Task<HttpStatusCode> Post(bool valid)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, receiver.Prefix) { Content = new ByteArrayContent(body) };
                request.Headers.Add("X-Signature", "sha1=" + Convert.ToHexString(HMACSHA1.HashData(Encoding.UTF8.GetBytes(valid ? token : "wrong"), body)).ToLowerInvariant());
                using var result = await http.SendAsync(request, stopping.Token);
                return result.StatusCode;
            }
            Check(await Post(false) == HttpStatusCode.Unauthorized, "wrong HMAC accepted");
            Check(await Post(true) == HttpStatusCode.OK, "valid event rejected");
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(response is not null && (await response).Status == "api_accepted", "send not confirmed");
            Check(await Post(true) == HttpStatusCode.OK, "duplicate request failed");
            Check(outgoingCalls == 1, "duplicate sent twice");
            stopping.Cancel(); await serving; await apiTask;
            Check(!receiver.IsRunning, "event server leaked");
        }
        finally { stopping.Cancel(); api.Close(); Directory.Delete(directory, true); }
    }

    private static async Task DurationValidation()
    {
        foreach (var seconds in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, .1, 9.99, 86400.01 })
        {
            var rejected = false;
            try { await GroupChatRuntime.RunAsync(new(), "10", seconds, false, "fixed", default); }
            catch (ArgumentException error) when (error.Message.StartsWith("chat seconds", StringComparison.Ordinal)) { rejected = true; }
            Check(rejected, "invalid chat duration reached setup or started");
        }
    }

    private static async Task PendingAmbientRuntime()
    {
        var directory = MakeLibrary();
        var previousDirectory = Environment.CurrentDirectory;
        var previousToken = Environment.GetEnvironmentVariable("ONEBOT_ACCESS_TOKEN");
        var previousOutput = Console.Out;
        using var output = new StringWriter();
        using var stopping = new CancellationTokenSource();
        var control = new AdminRuntimeControl();
        var clock = new AmbientClock();
        var eventPort = Port();
        Task? running = null;
        try
        {
            Environment.CurrentDirectory = directory;
            Environment.SetEnvironmentVariable("ONEBOT_ACCESS_TOKEN", "ambient-runtime-fixture");
            Console.SetOut(output);
            var settings = new GroupChatSettings { SelfId = 99, AllowedGroupIds = [10], EventPort = eventPort,
                Mode = "active", MentionIntervalSeconds = 3, ActiveIntervalSeconds = 3, ActiveMessageEvery = 1 };
            running = GroupChatRuntime.RunAsync(settings, "10", 0, false, "fixed", stopping.Token, clock, control);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            http.DefaultRequestHeaders.Authorization = new("Bearer", "ambient-runtime-fixture");
            async Task Post(long id, bool mention)
            {
                object[] segments = mention
                    ? [new { type = "at", data = new { qq = "99" } }, new { type = "text", data = new { text = "你好" } }]
                    : [new { type = "text", data = new { text = "今天下雨了" } }];
                var json = JsonSerializer.Serialize(new { post_type = "message", message_type = "group", self_id = 99, group_id = 10,
                    user_id = 20, message_id = id, time = clock.GetUtcNow().ToUnixTimeSeconds(), sender = new { user_id = 20 }, message = segments });
                using var response = await http.PostAsync($"http://127.0.0.1:{eventPort}/onebot/", new StringContent(json, Encoding.UTF8, "application/json"));
                Check(response.IsSuccessStatusCode, "runtime rejected fixture event");
            }
            JsonElement Snapshot() => JsonSerializer.SerializeToElement(control.Snapshot(), BridgeConfig.JsonOptions);
            bool Observed(long id, string status) => Snapshot().GetProperty("recentGroupActivity").EnumerateArray()
                .Any(item => item.GetProperty("triggerMessageId").GetInt64() == id && item.GetProperty("status").GetString() == status);
            async Task WaitFor(Func<bool> condition)
            {
                var until = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < until) { if (condition()) return; await Task.Delay(10); }
                throw new Exception("ambient runtime observation did not settle");
            }
            await Post(1, true); await WaitFor(() => Observed(1, "preview"));
            clock.Advance(TimeSpan.FromSeconds(1)); await Post(2, false);
            Check(!Observed(2, "preview") && Snapshot().GetProperty("policy").GetProperty("groups")[0].GetProperty("pendingActiveMessages").GetInt32() == 1,
                "ordinary message was sent during cooldown or not retained");
            clock.Advance(TimeSpan.FromSeconds(2));
            await WaitFor(() => Observed(2, "preview"));
            var observation = Snapshot().GetProperty("recentGroupActivity").EnumerateArray().Single(item =>
                item.GetProperty("triggerMessageId").GetInt64() == 2 && item.GetProperty("status").GetString() == "preview");
            Check(observation.GetProperty("trigger").GetString() == "ambient" && !observation.GetProperty("containsBotName").GetBoolean(),
                "ordinary timer reply mislabelled as addressed");
            await Post(3, false); await control.SetPausedAsync(true); clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(50);
            Check(!Observed(3, "preview") && Snapshot().GetProperty("policy").GetProperty("groups")[0].GetProperty("pendingActiveMessages").GetInt32() == 0,
                "paused ambient message was replayed");
            Check(!Snapshot().GetProperty("recentGroupActivity").EnumerateArray().Any(item => item.GetProperty("status").GetString() == "api_accepted"),
                "preview was reported as live send");
        }
        finally
        {
            stopping.Cancel();
            try { if (running is not null) await running.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { Console.SetOut(previousOutput); Environment.CurrentDirectory = previousDirectory;
                Environment.SetEnvironmentVariable("ONEBOT_ACCESS_TOKEN", previousToken); Directory.Delete(directory, true); }
        }
    }

    private sealed class AmbientClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }

    private static Task ContinuousRuntime() => RuntimeLifecycle(0);

    private static async Task BoundedRuntime()
    {
        foreach (var seconds in new[] { 10d, 600d, 86400d }) await RuntimeLifecycle(seconds);
    }

    private static async Task RuntimeLifecycle(double seconds)
    {
        var directory = MakeLibrary();
        var originalDirectory = Environment.CurrentDirectory;
        var originalToken = Environment.GetEnvironmentVariable("ONEBOT_ACCESS_TOKEN");
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        using var stopping = new CancellationTokenSource();
        using var fakeApi = new HttpListener();
        fakeApi.Prefixes.Add($"http://127.0.0.1:{Port()}/");
        fakeApi.Start();
        var unexpectedRequest = fakeApi.GetContextAsync();
        var eventPort = Port();
        while (fakeApi.Prefixes.Single().Contains($":{eventPort}/", StringComparison.Ordinal)) eventPort = Port();
        var prefix = $"http://127.0.0.1:{eventPort}/onebot/";
        var time = new ControlledTimeProvider();
        Task? running = null;
        try
        {
            Environment.CurrentDirectory = directory;
            Environment.SetEnvironmentVariable("ONEBOT_ACCESS_TOKEN", "isolated-runtime-check-token");
            Console.SetOut(output);
            var settings = new GroupChatSettings { SelfId = 99, AllowedGroupIds = [10], EventPort = eventPort,
                OneBotBaseUrl = fakeApi.Prefixes.Single() };
            running = GroupChatRuntime.RunAsync(settings, "10", seconds, false, "fixed", stopping.Token, time);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync(prefix);
            Check(response.StatusCode == HttpStatusCode.MethodNotAllowed && !running.IsCompleted, "runtime did not keep its listener alive");
            var lockPath = Path.Combine(directory, "Results/local/group-chat-session.lock");
            var locked = false;
            try { using var competing = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { locked = true; }
            Check(locked, "runtime did not hold the exclusive session lock");
            if (seconds == 0)
            {
                time.Advance(TimeSpan.FromDays(365));
                using var stillListening = await http.GetAsync(prefix);
                Check(stillListening.StatusCode == HttpStatusCode.MethodNotAllowed && !running.IsCompleted,
                    "continuous mode expired after advancing the deadline clock");
                stopping.Cancel();
            }
            else
            {
                time.Advance(TimeSpan.FromSeconds(seconds - 1));
                using var beforeDeadline = await http.GetAsync(prefix);
                Check(beforeDeadline.StatusCode == HttpStatusCode.MethodNotAllowed && !running.IsCompleted,
                    "bounded/default runtime expired before its deadline");
                time.Advance(TimeSpan.FromSeconds(1));
            }
            await running.WaitAsync(TimeSpan.FromSeconds(2));
            using (var released = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(released.CanWrite, "runtime retained session ownership after stopping");
            using var rebound = new HttpListener();
            rebound.Prefixes.Add(prefix); rebound.Start(); rebound.Close();
            Check(!unexpectedRequest.IsCompleted, "fixed preview made a OneBot API request");
            Check(!File.Exists(Path.Combine(directory, "Results/local/deepseek-budget.json")), "lifecycle check reserved a paid API call");
        }
        finally
        {
            stopping.Cancel();
            try { if (running is not null) await running.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally
            {
                Console.SetOut(originalOutput);
                Environment.CurrentDirectory = originalDirectory;
                Environment.SetEnvironmentVariable("ONEBOT_ACCESS_TOKEN", originalToken);
                fakeApi.Close();
                try { await unexpectedRequest; } catch (Exception error) when (error is HttpListenerException or ObjectDisposedException) { }
                var resolved = Path.GetFullPath(directory);
                Check(resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "unexpected cleanup directory");
                Directory.Delete(resolved, true);
            }
        }
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private ControlledTimer? timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            timer = new ControlledTimer(callback, state, dueTime);
            return timer;
        }
        public void Advance(TimeSpan elapsed) => timer?.Advance(elapsed);
        private sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private bool disposed;
            private TimeSpan remaining = dueTime;
            public void Advance(TimeSpan elapsed)
            {
                if (disposed || remaining == Timeout.InfiniteTimeSpan) return;
                remaining -= elapsed;
                if (remaining > TimeSpan.Zero) return;
                remaining = Timeout.InfiniteTimeSpan;
                callback(state);
            }
            public bool Change(TimeSpan nextDueTime, TimeSpan period)
            {
                if (disposed) return false;
                remaining = nextDueTime;
                return true;
            }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private const string Json = "{\"send\":true,\"text\":\"收到。\",\"memeId\":\"roger\"}";
    private static GroupChatPolicy Policy() => new(new() { AllowedGroupIds = [10], SelfId = 99, BotNames = ["机器人"] });
    private static ParsedGroupMessage Message(long id, string text = "机器人你好") => new(10, 20, id, DateTimeOffset.UtcNow, text, true, false);
    private static string MakeLibrary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qq-messaging-check-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(directory, "Files/Memes"));
        File.WriteAllBytes(Path.Combine(directory, "Files/Memes/roger.png"), MemeLibraryChecks.Png);
        File.WriteAllText(Path.Combine(directory, "Files/Memes/manifest.json"), "{\"version\":1,\"items\":[{\"id\":\"roger\",\"file\":\"roger.png\",\"tags\":[\"收到\"]}]}");
        return directory;
    }
    private static int Port() { var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); var value = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop(); return value; }
    private static TaskCompletionSource Ready() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private sealed class SlowReply(TaskCompletionSource started, TaskCompletionSource release, string response = Json) : IStreamingReply
    {
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history, [EnumeratorCancellation] CancellationToken ct)
        { started.TrySetResult(); await release.Task; yield return response; }
    }
    private sealed class FakeApi : HttpMessageHandler
    {
        public int Calls;
        public int Sends;
        public long Account = 99;
        public string? LastSendBody;
        public bool FailNextSend;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var login = request.RequestUri!.AbsolutePath == "/get_login_info";
            if (!login) { Interlocked.Increment(ref Sends); LastSendBody = await request.Content!.ReadAsStringAsync(ct); }
            if (!login && FailNextSend)
            {
                FailNextSend = false;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"failed\",\"retcode\":1}") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                login ? "{\"status\":\"ok\",\"retcode\":0,\"data\":{\"user_id\":" + Account + "}}" : "{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":1}}") };
        }
    }
}
