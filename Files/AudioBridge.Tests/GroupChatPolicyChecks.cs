using System.Text.Json;
using System.Text.Json.Nodes;
using AudioBridge;

public static class GroupChatPolicyChecks
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private static GroupChatPolicyOptions Options(GroupChatMode mode = GroupChatMode.Mention) =>
        new() { SelfId = 99, AllowedGroupIds = new long[] { 10, 11 }, BotNames = ["小深", "Bot"], Mode = mode, ActiveMessageEvery = 1 };
    private static GroupChatPolicy Policy(GroupChatMode mode = GroupChatMode.Mention) => new(Options(mode), () => Now);
    private static ParsedGroupMessage Message(long id, DateTimeOffset? at = null, long group = 10,
        bool mentioned = true, string text = "请帮我看看？", long sender = 20) => new(group, sender, id, at ?? Now, text, mentioned, false);
    private static void Assert(bool condition, string why) { if (!condition) throw new Exception(why); }

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("OneBot identity/type/time filters and safe summaries", ParseFilters),
            ("sender card and nickname label speakers without becoming instructions", SenderNames),
            ("mention/name/active/manual trigger separation", Modes),
            ("concurrent duplicate reserves exactly one candidate", ConcurrentDuplicate),
            ("concurrent different messages cannot overlap a group reservation", ConcurrentCandidates),
            ("group contexts and reservations are isolated", CrossGroup),
            ("busy messages become context, never a delayed reply queue", BusyContext),
            ("mention cooldown and hourly reservations count failures", MentionRate),
            ("active mode enforces 120 seconds and six per hour", ActiveRate),
            ("active mode routes structured mentions and name prefixes through the eight-second direct limit", ActiveDirectRate),
            ("direct replies do not reset ambient cooldown and both retain a short inter-reply gap", CrossRouteCooldown),
            ("ordinary short messages earn sampled ambient opportunities without keywords or duplicate spend", AmbientSampling),
            ("sample counters isolate groups and pause discards partial opportunities", AmbientSamplingIsolation),
            ("busy ordinary messages merge into one current opportunity and the next bounded batch", AmbientBusyDeferred),
            ("partial and complete ambient samples expire at two minutes without new messages", AmbientExpiry),
            ("pause and hourly exhaustion discard ambient work without a later backlog", AmbientDiscard),
            ("direct and ambient hourly allowances are independent and expire on rolling boundaries", IndependentRateBudgets),
            ("configured three-second group routes honor sixty and 120 rolling-hour reservations", ConfigurableRateBoundaries),
            ("persisted settings accept shared rate boundaries and reject values outside them", SettingsRateBoundaries),
            ("expired or paused reservations stay busy and never revive", ReservationLifecycle),
            ("fresh dedup capacity fails closed and TTL prunes", EventCapacity),
            ("context/text bounds and Unicode boundary are preserved", ContextBounds),
            ("acknowledged own replies are bounded context, never new events", OwnReplyContext),
            ("clock rollback cannot bypass cooldown", ClockRollback),
            ("configuration is validated and defensively copied", Configuration)
        };
        foreach (var check in checks)
        {
            await check.Run().WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"PASS group policy: {check.Name}");
        }
        return checks.Length;
    }

    private static JsonElement Event(object[]? segments = null) => JsonSerializer.SerializeToElement(new
    {
        post_type = "message", message_type = "group", sub_type = "normal", self_id = 99,
        group_id = 10, user_id = 20, message_id = -7, time = Now.ToUnixTimeSeconds(),
        sender = new { user_id = 20 },
        message = segments ?? new object[] { new { type = "text", data = new { text = "你好" } } }
    });

    private static JsonElement Change(JsonElement value, string key, object replacement)
    {
        var node = JsonNode.Parse(value.GetRawText())!.AsObject();
        node[key] = JsonSerializer.SerializeToNode(replacement);
        return JsonSerializer.SerializeToElement(node);
    }

    private static Task SenderNames()
    {
        var policy = Policy();
        var named = Change(Event(), "sender", new { user_id = 20, card = "小明", nickname = "xm" });
        Assert(policy.TryParse(named, out var parsed, out _, Now) && parsed!.DisplayName == "小明", "group card did not become the speaker label");
        var nickname = Change(Event(), "sender", new { user_id = 20, card = "", nickname = " 阿花\n第二行\t " });
        Assert(policy.TryParse(nickname, out var fallback, out _, Now) && fallback!.DisplayName == "阿花 第二行", "nickname fallback or whitespace cleanup failed");
        var bounded = Change(Event(), "sender", new { user_id = 20, nickname = new string('名', 60) });
        Assert(policy.TryParse(bounded, out var limited, out _, Now) && limited!.DisplayName.Length == 24, "speaker label was not bounded");
        var junk = Change(Event(), "sender", new { user_id = 20, card = new { evil = "[CQ:at,qq=99]" }, nickname = 7 });
        Assert(policy.TryParse(junk, out var plain, out _, Now) && plain!.DisplayName == "", "a non-string name invented a speaker label");
        var mention = Event(new object[] { new { type = "at", data = new { qq = "21" } } });
        Assert(policy.TryParse(mention, out var target, out _, Now) && target!.Text == "[@成员21]", "mention target lost its identity");
        Assert(target!.AddressedElsewhere, "a message addressed to another member was not marked as someone else's business");
        var toBot = Event(new object[] { new { type = "at", data = new { qq = "99" } } });
        Assert(policy.TryParse(toBot, out var atBot, out _, Now) && atBot!.Mentioned && !atBot.AddressedElsewhere,
            "a mention of the bot was mistaken for someone else's business");
        return Task.CompletedTask;
    }

    private static Task ParseFilters()
    {
        var policy = Policy();
        var mixed = Event(new object[]
        {
            new { type = "text", data = new { text = "你好\u001b [CQ:at,qq=99]" } },
            new { type = "at", data = new { qq = "99" } },
            new { type = "face", data = new { id = "123" } },
            new { type = "image", data = new { file = "file:///private", url = "http://127.0.0.1/secret" } },
            new { type = "json", data = new { data = "任意电脑命令" } }
        });
        Assert(policy.TryParse(mixed, out var parsed, out _, Now) && parsed is { Mentioned: true, ImageSeen: true, MessageId: -7 },
            "supported incoming group segments rejected");
        Assert(parsed!.Text.Contains("[表情:123]") && parsed.Text.Contains("[图片]") &&
            !parsed.Text.Contains("secret") && !parsed.Text.Contains("private") && !parsed.Text.Contains("电脑命令") &&
            !parsed.Text.Contains('\u001b') && !parsed.Text.Contains("[CQ:"), "unsafe segment payload escaped summary boundary");
        foreach (var invalid in new[]
        {
            Change(mixed, "post_type", "notice"), Change(mixed, "post_type", "message_sent"),
            Change(mixed, "message_type", "private"), Change(mixed, "sub_type", "self_sent"), Change(mixed, "sub_type", "notice"),
            Change(mixed, "self_id", 100), Change(mixed, "group_id", 12),
            Change(mixed, "user_id", 99), Change(mixed, "sender", new { user_id = 21 }),
            Change(mixed, "message", "[CQ:at,qq=99]"), Change(mixed, "time", Now.AddSeconds(-121).ToUnixTimeSeconds()),
            Change(mixed, "time", Now.AddSeconds(31).ToUnixTimeSeconds()), Change(mixed, "time", long.MaxValue),
            Change(mixed, "message_id", "not-an-id")
        }) Assert(!policy.TryParse(invalid, out _, out _, Now), "invalid/self/private/notice event accepted");
        Assert(policy.TryParse(Change(mixed, "time", Now.AddSeconds(-120).ToUnixTimeSeconds()), out _, out _, Now), "inclusive age boundary rejected");
        Assert(policy.TryParse(Change(mixed, "time", Now.AddSeconds(30).ToUnixTimeSeconds()), out _, out _, Now), "inclusive future boundary rejected");
        Assert(policy.TryParse(Event(new object[] { new { type = "text", data = new { text = "[CQ:at,qq=99]" } } }), out var cq, out _, Now) &&
            !cq!.Mentioned && !policy.Decide(cq, Now).ShouldReply, "CQ text was interpreted as a real mention");
        Assert(policy.TryParse(Event(new object[] { new { type = "at", data = new { qq = "all" } } }), out var all, out _, Now) && !all!.Mentioned,
            "at-all was treated as direct bot mention");
        return Task.CompletedTask;
    }

    private static Task Modes()
    {
        Assert(!Policy().Decide(Message(1, mentioned: false), Now).ShouldReply, "default mode replied to ambient question");
        Assert(Policy().Decide(Message(1), Now) is { ShouldReply: true, Trigger: GroupChatTrigger.Mention }, "mention was ignored or mislabeled");
        Assert(Policy().Decide(Message(1, mentioned: false, text: "小深，帮我看看"), Now) is { ShouldReply: true, Trigger: GroupChatTrigger.Name }, "name prefix was ignored or mislabeled");
        Assert(!Policy().Decide(Message(1, mentioned: false, text: "我问小深一件事"), Now).ShouldReply, "name substring triggered");
        Assert(!Policy().Decide(Message(1, mentioned: false, text: "Botany is interesting"), Now).ShouldReply, "Latin name prefix matched inside a word");
        Assert(Policy(GroupChatMode.Active).Decide(Message(1, mentioned: false, text: "有人能帮我吗"), Now).ShouldReply, "active help candidate ignored");
        Assert(Policy(GroupChatMode.Active).Decide(Message(1, mentioned: false, text: "大家晚上好"), Now) is { ShouldReply: true, Trigger: GroupChatTrigger.Ambient }, "active mode never offered substantive chatter or mislabeled it");
        Assert(Policy(GroupChatMode.Active).Decide(Message(1, mentioned: false, text: "哈哈"), Now).ShouldReply, "reaction candidate ignored");
        var toSomeoneElse = Message(1, mentioned: false, text: "[@成员21] 你看这个") with { AddressedElsewhere = true };
        Assert(!Policy(GroupChatMode.Active).Decide(toSomeoneElse, Now).ShouldReply, "a message addressed to another member was answered");
        Assert(Policy(GroupChatMode.Active).Decide(Message(2, mentioned: false, text: "大家晚上好"), Now).ShouldReply,
            "an ordinary message after someone else's business stopped being a candidate");
        Assert(!Policy(GroupChatMode.Active).Decide(Message(1, mentioned: false, text: "[图片]"), Now).ShouldReply, "bare image spent AI candidate budget");
        Assert(!Policy(GroupChatMode.Active).Decide(Message(1, mentioned: false, text: "嗯"), Now).ShouldReply, "one-character particle spent an ambient opportunity");
        Assert(!Policy(GroupChatMode.Manual).Decide(Message(1), Now).ShouldReply, "manual mode auto-replied");
        Assert(!Policy().Decide(Message(1, sender: 99), Now).ShouldReply &&
            !Policy().Decide(Message(1, group: 12), Now).ShouldReply, "Decide trusted forged parsed identity");
        var privatePolicy = new GroupChatPolicy(Options(GroupChatMode.Active) with { AllowedPrivateUserIds = [20] }, () => Now);
        Assert(privatePolicy.Decide(Message(1, group: 0) with { IsPrivate = true }, Now) is
            { ShouldReply: true, Trigger: GroupChatTrigger.Private }, "private message inherited a group trigger");
        return Task.CompletedTask;
    }

    private static async Task ConcurrentDuplicate()
    {
        var policy = Policy();
        var results = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => Task.Run(() => policy.Decide(Message(1), Now))));
        var admitted = results.Single(result => result.ShouldReply);
        Assert(results.Count(result => result.Reason == "duplicate") == 59 && policy.Snapshot(Now).Groups.Single().ContextCount == 1,
            "concurrent duplicate reached API reservation or repeated context");
        Assert(!policy.Complete(Guid.NewGuid(), true, Now), "unknown completion released busy");
        Assert(policy.Complete(admitted.ReservationId!.Value, false, Now), "failed candidate completion not accepted");
        Assert(policy.Decide(Message(1, Now.AddSeconds(9)), Now.AddSeconds(9)).Reason == "duplicate", "failed message retried");
    }

    private static async Task ConcurrentCandidates()
    {
        var policy = Policy();
        var results = await Task.WhenAll(Enumerable.Range(1, 50).Select(id => Task.Run(() => policy.Decide(Message(id), Now))));
        Assert(results.Count(result => result.ShouldReply) == 1 && results.Count(result => result.Reason == "busy") == 49,
            "different concurrent events created overlapping replies");
        Assert(policy.Snapshot(Now).SeenEvents == 50, "busy events not consumed");
    }

    private static Task CrossGroup()
    {
        var policy = Policy();
        var a = policy.Decide(Message(1, group: 10, text: "甲群问题"), Now);
        var b = policy.Decide(Message(1, group: 11, text: "乙群问题"), Now);
        Assert(a.ShouldReply && b.ShouldReply && a.ReservationId != b.ReservationId, "group busy/dedup leaked across groups");
        Assert(a.Context.All(m => m.GroupId == 10) && b.Context.All(m => m.GroupId == 11), "context leaked across groups");
        policy.Complete(a.ReservationId!.Value, true, Now);
        Assert(policy.IsCurrent(b.ReservationId!.Value, Now), "completing one group released the other");
        return Task.CompletedTask;
    }

    private static Task BusyContext()
    {
        var policy = Policy();
        var first = policy.Decide(Message(1), Now);
        var busy = policy.Decide(Message(2, Now.AddSeconds(9), text: "忙时新问题？"), Now.AddSeconds(9));
        Assert(!busy.ShouldReply && busy.Reason == "busy" && busy.Context.Count == 2, "busy event was not context-only");
        policy.Complete(first.ReservationId!.Value, true, Now.AddSeconds(10));
        Assert(policy.Decide(busy.Message, Now.AddSeconds(11)).Reason == "duplicate", "busy message replay became a queued reply");
        Assert(policy.Decide(Message(3, Now.AddSeconds(11)), Now.AddSeconds(11)).ShouldReply, "new event could not proceed after cleanup");
        return Task.CompletedTask;
    }

    private static Task MentionRate()
    {
        var policy = new GroupChatPolicy(Options() with { MentionPerHour = 2 }, () => Now);
        var first = policy.Decide(Message(1), Now); policy.Complete(first.ReservationId!.Value, false, Now);
        Assert(policy.Decide(Message(2, Now.AddSeconds(7)), Now.AddSeconds(7)).Reason == "cooldown", "mention cooldown bypassed");
        var second = policy.Decide(Message(3, Now.AddSeconds(8)), Now.AddSeconds(8));
        Assert(second.ShouldReply, "8-second boundary rejected"); policy.Complete(second.ReservationId!.Value, true, Now.AddSeconds(8));
        Assert(policy.Decide(Message(4, Now.AddSeconds(16)), Now.AddSeconds(16)).Reason == "hourly_limit", "failure did not consume hourly attempt");
        Assert(policy.Decide(Message(5, Now.AddHours(1)), Now.AddHours(1)).ShouldReply, "rolling hourly window did not expire");
        return Task.CompletedTask;
    }

    private static Task ActiveRate()
    {
        var policy = Policy(GroupChatMode.Active);
        for (var i = 0; i < 6; i++)
        {
            var at = Now.AddSeconds(i * 120);
            var decision = policy.Decide(Message(i, at, mentioned: false), at);
            Assert(decision.ShouldReply, "valid active interval rejected"); policy.Complete(decision.ReservationId!.Value, false, at);
            if (i == 0) Assert(policy.Decide(Message(100, at.AddSeconds(119), mentioned: false), at.AddSeconds(119)).Reason == "active_cooldown", "active interval bypassed");
        }
        Assert(policy.Decide(Message(9, Now.AddSeconds(720), mentioned: false), Now.AddSeconds(720)).Reason == "hourly_limit", "active exceeded six per hour");
        Assert(policy.Decide(Message(10, Now.AddSeconds(3599), mentioned: false), Now.AddSeconds(3599)).Reason == "hourly_limit", "active attempt expired before one hour");
        var renewed = policy.Decide(Message(11, Now.AddHours(1), mentioned: false), Now.AddHours(1));
        Assert(renewed.ShouldReply, "active rolling window did not expire at exactly one hour");
        policy.Complete(renewed.ReservationId!.Value, false, Now.AddHours(1));
        var status = policy.Snapshot(Now.AddHours(1)).Groups.Single();
        Assert(status.ActiveReservedLastHour == 6 && status.DirectReservedLastHour == 0 && status.ReservedLastHour == 6,
            "active failures or rolling counters were lost");
        return Task.CompletedTask;
    }

    private static Task ActiveDirectRate()
    {
        var policy = Policy(GroupChatMode.Active);
        var incoming = Event(new object[]
        {
            new { type = "at", data = new { qq = "99" } },
            new { type = "text", data = new { text = "你是谁" } }
        });
        Assert(policy.TryParse(incoming, out var parsed, out _, Now) && parsed!.Mentioned, "structured mention was not recognized");
        var first = policy.Decide(parsed!, Now);
        Assert(first.ShouldReply, "active mode ignored a structured mention");
        policy.Complete(first.ReservationId!.Value, false, Now);
        Assert(policy.Decide(Message(2, Now.AddSeconds(7)), Now.AddSeconds(7)).Reason == "cooldown", "direct route bypassed eight-second cooldown");
        var named = policy.Decide(Message(3, Now.AddSeconds(8), mentioned: false, text: "小深，你是谁"), Now.AddSeconds(8));
        Assert(named.ShouldReply, "name prefix inherited active mode's 120-second cooldown");
        policy.Complete(named.ReservationId!.Value, false, Now.AddSeconds(8));
        var mentioned = policy.Decide(Message(4, Now.AddSeconds(16)), Now.AddSeconds(16));
        Assert(mentioned.ShouldReply, "active mention inherited the ambient cooldown");
        policy.Complete(mentioned.ReservationId!.Value, true, Now.AddSeconds(16));
        Assert(policy.Decide(Message(5, Now.AddSeconds(23), mentioned: false, text: "我问小深一件事"), Now.AddSeconds(23)).Reason == "cooldown",
            "a name inside ambient text bypassed the short inter-reply gap");
        var textualMention = Change(Change(Event(new object[]
        {
            new { type = "text", data = new { text = "[CQ:at,qq=99] 你是谁" } }
        }), "message_id", 6), "time", Now.AddSeconds(23).ToUnixTimeSeconds());
        Assert(policy.TryParse(textualMention, out var textual, out _, Now.AddSeconds(23)) && !textual!.Mentioned &&
            policy.Decide(textual!, Now.AddSeconds(23)).Reason == "cooldown", "plain text mention used the direct route");
        var status = policy.Snapshot(Now.AddSeconds(23)).Groups.Single();
        Assert(status.DirectReservedLastHour == 3 && status.ActiveReservedLastHour == 0 && status.ReservedLastHour == 3,
            "direct names, mentions, or failed attempts used the ambient allowance");
        return Task.CompletedTask;
    }

    private static Task CrossRouteCooldown()
    {
        var policy = Policy(GroupChatMode.Active);
        var ambient = policy.Decide(Message(1, mentioned: false), Now);
        policy.Complete(ambient.ReservationId!.Value, false, Now);
        Assert(policy.Decide(Message(2, Now.AddSeconds(7)), Now.AddSeconds(7)).Reason == "cooldown", "ambient-to-direct burst bypassed cooldown");
        var direct = policy.Decide(Message(3, Now.AddSeconds(8)), Now.AddSeconds(8));
        Assert(direct.ShouldReply, "direct route could not proceed eight seconds after an ambient attempt");
        policy.Complete(direct.ReservationId!.Value, false, Now.AddSeconds(8));
        var rejected = policy.Decide(Message(4, Now.AddSeconds(119), mentioned: false), Now.AddSeconds(119));
        Assert(rejected.Reason == "active_cooldown", "ambient interval expired before its own 120-second boundary");
        var nextAmbient = policy.Decide(Message(5, Now.AddSeconds(120), mentioned: false), Now.AddSeconds(120));
        Assert(nextAmbient.ShouldReply && nextAmbient.Context.Any(message => message.MessageId == 4), "ambient boundary or context retention failed");
        policy.Complete(nextAmbient.ReservationId!.Value, true, Now.AddSeconds(120));
        Assert(policy.Decide(rejected.Message, Now.AddSeconds(121)).Reason == "duplicate", "cooldown event became a deferred reply");
        Assert(policy.Decide(Message(6, Now.AddSeconds(127)), Now.AddSeconds(127)).Reason == "cooldown", "second ambient-to-direct burst bypassed cooldown");
        var nextDirect = policy.Decide(Message(7, Now.AddSeconds(128)), Now.AddSeconds(128));
        Assert(nextDirect.ShouldReply, "direct route's eight-second boundary after ambient reply failed");
        policy.Complete(nextDirect.ReservationId!.Value, true, Now.AddSeconds(128));
        var status = policy.Snapshot(Now.AddSeconds(128)).Groups.Single();
        Assert(status.DirectReservedLastHour == 2 && status.ActiveReservedLastHour == 2 && status.ReservedLastHour == 4 &&
            status.LastReservedAt == Now.AddSeconds(128) && status.LastActiveReservedAt == Now.AddSeconds(120),
            "direct reply reset the separate ambient timestamp or altered counters");
        return Task.CompletedTask;
    }

    private static Task IndependentRateBudgets()
    {
        var directPolicy = Policy(GroupChatMode.Active);
        for (var i = 0; i < 30; i++)
        {
            var at = Now.AddSeconds(i * 8);
            var decision = directPolicy.Decide(Message(i, at), at);
            Assert(decision.ShouldReply, "direct route stopped before thirty attempts");
            directPolicy.Complete(decision.ReservationId!.Value, false, at);
        }
        Assert(directPolicy.Decide(Message(100, Now.AddSeconds(240)), Now.AddSeconds(240)).Reason == "hourly_limit", "direct failures did not consume all thirty attempts");
        var ambientAt = Now.AddSeconds(352);
        var ambient = directPolicy.Decide(Message(101, ambientAt, mentioned: false), ambientAt);
        Assert(ambient.ShouldReply, "exhausting direct allowance also exhausted ambient allowance");
        directPolicy.Complete(ambient.ReservationId!.Value, false, ambientAt);
        Assert(directPolicy.Decide(Message(102, Now.AddSeconds(3599)), Now.AddSeconds(3599)).Reason == "hourly_limit", "direct attempts expired before the rolling hour");
        var renewalAt = Now.AddHours(1);
        var renewed = directPolicy.Decide(Message(103, renewalAt), renewalAt);
        Assert(renewed.ShouldReply, "direct allowance did not renew at the exact rolling-hour boundary");
        directPolicy.Complete(renewed.ReservationId!.Value, false, renewalAt);
        var status = directPolicy.Snapshot(renewalAt).Groups.Single();
        Assert(status.DirectReservedLastHour == 30 && status.ActiveReservedLastHour == 1 && status.ReservedLastHour == 31,
            "hour boundary reset all attempts or status omitted the independent counters");
        var nextAt = renewalAt.AddSeconds(8);
        var next = directPolicy.Decide(Message(104, nextAt), nextAt);
        Assert(next.ShouldReply, "second direct attempt did not expire independently on the rolling window");
        directPolicy.Complete(next.ReservationId!.Value, false, nextAt);
        Assert(directPolicy.Snapshot(nextAt).Groups.Single().DirectReservedLastHour == 30, "rolling expiry erased too many direct attempts");

        var activePolicy = Policy(GroupChatMode.Active);
        for (var i = 0; i < 6; i++)
        {
            var at = Now.AddSeconds(i * 120);
            var decision = activePolicy.Decide(Message(i, at, mentioned: false), at);
            Assert(decision.ShouldReply, "ambient route stopped before six attempts");
            activePolicy.Complete(decision.ReservationId!.Value, false, at);
        }
        var directAt = Now.AddSeconds(608);
        var direct = activePolicy.Decide(Message(110, directAt), directAt);
        Assert(direct.ShouldReply, "ambient hourly cap blocked a direct mention");
        activePolicy.Complete(direct.ReservationId!.Value, false, directAt);
        var blockedAt = Now.AddSeconds(728);
        Assert(activePolicy.Decide(Message(111, blockedAt, mentioned: false), blockedAt).Reason == "hourly_limit", "ambient failures lost their six-attempt cap after a direct reply");
        var activeStatus = activePolicy.Snapshot(blockedAt).Groups.Single();
        Assert(activeStatus.DirectReservedLastHour == 1 && activeStatus.ActiveReservedLastHour == 6 && activeStatus.ReservedLastHour == 7,
            "ambient hourly cap consumed direct allowance or broke aggregate status");
        return Task.CompletedTask;
    }

    private static Task AmbientSampling()
    {
        var policy = new GroupChatPolicy(Options(GroupChatMode.Active) with
        {
            MentionInterval = TimeSpan.FromSeconds(3), ActiveInterval = TimeSpan.FromSeconds(60), ActiveMessageEvery = 3
        }, () => Now);
        GroupChatDecision Ambient(long id, double seconds, string text) =>
            policy.Decide(Message(id, Now.AddSeconds(seconds), mentioned: false, text: text), Now.AddSeconds(seconds));
        Assert(Ambient(1, 0, "今天好累") is { Reason: "active_waiting", Trigger: GroupChatTrigger.Ambient },
            "first ordinary message spent a model attempt or lost its trigger classification");
        Assert(Ambient(1, 0, "今天好累").Reason == "duplicate", "duplicate ordinary message counted as another sample");
        foreach (var (id, text) in new[] { (2, "[图片]"), (3, "？！"), (4, "嗯") })
            Assert(Ambient(id, id - 1, text).Reason == "not_a_candidate", "nontext or one-character noise earned an opportunity");
        Assert(Ambient(2000, 3, "[表情:123]！！！").Reason == "not_a_candidate" &&
            Ambient(2001, 3, "[图片]嗯").Reason == "not_a_candidate",
            "image/face summary labels or IDs were counted as ordinary words");
        Assert(Ambient(5, 4, "好困").Reason == "active_waiting", "keyword-free two-character sentence did not enter sampling");
        var direct = policy.Decide(Message(6, Now.AddSeconds(5)), Now.AddSeconds(5));
        Assert(direct.ShouldReply, "ambient sampling blocked a direct mention");
        policy.Complete(direct.ReservationId!.Value, false, Now.AddSeconds(5));
        Assert(Ambient(7, 6, "想休息").Reason == "cooldown" &&
            policy.Snapshot(Now.AddSeconds(6)).Groups.Single().ActiveMessagesSinceOpportunity == 3,
            "short gap discarded a sample or allowed an immediate ambient reply");
        Assert(policy.TakeReadyActiveReplies(Now.AddSeconds(7.999)).Count == 0,
            "deferred ambient batch bypassed the short direct-reply gap");
        var first = policy.TakeReadyActiveReplies(Now.AddSeconds(8)).Single();
        Assert(first.ShouldReply && first.Trigger == GroupChatTrigger.Ambient && first.Message.MessageId == 7,
            "third ordinary sentence never became ready without another incoming event");
        policy.Complete(first.ReservationId!.Value, false, Now.AddSeconds(8));
        foreach (var seconds in new[] { 30, 50, 64 })
        {
            var mention = policy.Decide(Message(100 + seconds, Now.AddSeconds(seconds)), Now.AddSeconds(seconds));
            Assert(mention.ShouldReply, "ambient cooldown also blocked direct replies");
            policy.Complete(mention.ReservationId!.Value, false, Now.AddSeconds(seconds));
        }
        Assert(Ambient(9, 65, "下课啦").Reason == "cooldown" && Ambient(10, 66, "回寝室").Reason == "cooldown" &&
            Ambient(11, 67, "吃饭啦").Reason == "active_cooldown", "ordinary cooldown routes were not preserved");
        var pending = policy.Snapshot(Now.AddSeconds(67)).Groups.Single();
        Assert(pending.ActiveMessagesSinceOpportunity == 3 && pending.PendingActiveMessages == 3 &&
            pending.ActiveDueAt == Now.AddSeconds(68) && pending.ActiveTriggerMessageId == 11,
            "cooldown discarded ordinary messages or hid their next opportunity");
        Assert(policy.TakeReadyActiveReplies(Now.AddSeconds(67.999)).Count == 0, "ambient cooldown expired early");
        var second = policy.TakeReadyActiveReplies(Now.AddSeconds(68)).Single();
        Assert(second.ShouldReply && second.Message.MessageId == 11 && second.Context.Any(item => item.MessageId == 164),
            "timer-driven ordinary opportunity lost the latest trigger or current direct-reply context");
        Assert(policy.TakeReadyActiveReplies(Now.AddSeconds(68)).Count == 0, "polling reserved the same batch twice");
        policy.Complete(second.ReservationId!.Value, false, Now.AddSeconds(68));
        var status = policy.Snapshot(Now.AddSeconds(68)).Groups.Single();
        Assert(status.ActiveMessageEvery == 3 && status.ActiveMessagesSinceOpportunity == 0 &&
            status.ActiveReservedLastHour == 2 && status.DirectReservedLastHour == 4 && status.LastActiveReservedAt == Now.AddSeconds(68) &&
            status.ActiveTriggerMessageId is null && status.ActiveDueAt is null,
            "sampling consumed extra model attempts or omitted diagnostic state");
        return Task.CompletedTask;
    }

    private static Task AmbientBusyDeferred()
    {
        var policy = new GroupChatPolicy(Options(GroupChatMode.Active) with
        { MentionInterval = TimeSpan.FromSeconds(3), ActiveInterval = TimeSpan.FromSeconds(60), ActiveMessageEvery = 3 }, () => Now);
        var direct = policy.Decide(Message(1), Now);
        GroupChatDecision Ambient(long id, int seconds) =>
            policy.Decide(Message(id, Now.AddSeconds(seconds), mentioned: false, text: "今天终于下课啦"), Now.AddSeconds(seconds));
        for (var id = 2; id <= 6; id++) Assert(Ambient(id, id - 1).Reason == "busy", "busy ordinary batch overlapped generation");
        Assert(Ambient(6, 5).Reason == "duplicate", "ordinary duplicate entered the pending batch twice");
        var pending = policy.Snapshot(Now.AddSeconds(5)).Groups.Single();
        Assert(pending.PendingActiveMessages == 3 && pending.ActiveTriggerMessageId == 6 && pending.ActiveReservedLastHour == 0 &&
            pending.ActiveDueAt is null &&
            policy.TakeReadyActiveReplies(Now.AddSeconds(5)).Count == 0,
            "busy ordinary samples were lost, unbounded, or spent an attempt");
        policy.Complete(direct.ReservationId!.Value, false, Now.AddSeconds(5));
        var first = policy.TakeReadyActiveReplies(Now.AddSeconds(5)).Single();
        Assert(first.Trigger == GroupChatTrigger.Ambient && first.Message.MessageId == 6 && first.Context.Count == 6 &&
            policy.TakeReadyActiveReplies(Now.AddSeconds(5)).Count == 0,
            "busy ordinary messages failed to merge into exactly one latest-trigger opportunity");
        for (var id = 7; id <= 11; id++) Assert(Ambient(id, id).Reason == "busy", "generation lost ownership while the next batch accumulated");
        Assert(policy.Snapshot(Now.AddSeconds(11)).Groups.Single().PendingActiveMessages == 3,
            "messages during ambient generation did not build one bounded next batch");
        policy.Complete(first.ReservationId!.Value, false, Now.AddSeconds(11));
        Assert(policy.TakeReadyActiveReplies(Now.AddSeconds(64.999)).Count == 0, "next batch bypassed ambient cooldown");
        var second = policy.TakeReadyActiveReplies(Now.AddSeconds(65)).Single();
        Assert(second.Message.MessageId == 11 && second.Context.Last().MessageId == 11,
            "next batch used an older trigger or reservation-time context");
        policy.Complete(second.ReservationId!.Value, false, Now.AddSeconds(65));
        Assert(policy.TakeReadyActiveReplies(Now.AddSeconds(125)).Count == 0 &&
            policy.Snapshot(Now.AddSeconds(125)).Groups.Single().ActiveReservedLastHour == 2,
            "merged ordinary batches became a multi-reply backlog");
        return Task.CompletedTask;
    }

    private static Task AmbientExpiry()
    {
        var policy = new GroupChatPolicy(Options(GroupChatMode.Active) with { ActiveMessageEvery = 3 }, () => Now);
        Assert(policy.Decide(Message(1, mentioned: false), Now).Reason == "active_waiting" &&
            policy.Decide(Message(2, Now.AddSeconds(1), mentioned: false), Now.AddSeconds(1)).Reason == "active_waiting",
            "partial sample setup failed");
        Assert(policy.Snapshot(Now.AddSeconds(120)).Groups.Single().PendingActiveMessages == 2,
            "inclusive two-minute sample boundary was lost");
        Assert(policy.Snapshot(Now.AddSeconds(121)).Groups.Single().PendingActiveMessages == 1,
            "old partial sample stayed credited after two minutes");
        Assert(policy.Decide(Message(3, Now.AddSeconds(122), mentioned: false), Now.AddSeconds(122)).Reason == "active_waiting" &&
            policy.Snapshot(Now.AddSeconds(122)).Groups.Single().PendingActiveMessages == 1,
            "expired partial samples helped a new message reach its threshold");
        var expired = policy.Snapshot(Now.AddSeconds(243)).Groups.Single();
        Assert(expired.PendingActiveMessages == 0 && expired.ActiveTriggerMessageId is null && expired.ActiveDueAt is null &&
            policy.TakeReadyActiveReplies(Now.AddSeconds(243)).Count == 0, "stale partial trigger survived idle expiry");

        var busyPolicy = new GroupChatPolicy(Options(GroupChatMode.Active) with { ActiveMessageEvery = 3 }, () => Now);
        var direct = busyPolicy.Decide(Message(1), Now);
        for (var id = 2; id <= 4; id++) busyPolicy.Decide(Message(id, Now.AddSeconds(id), mentioned: false), Now.AddSeconds(id));
        Assert(busyPolicy.Snapshot(Now.AddSeconds(124)).Groups.Single().PendingActiveMessages == 1,
            "mature waiting samples did not age independently");
        busyPolicy.Complete(direct.ReservationId!.Value, false, Now.AddSeconds(125));
        Assert(busyPolicy.TakeReadyActiveReplies(Now.AddSeconds(125)).Count == 0 &&
            busyPolicy.Snapshot(Now.AddSeconds(125)).Groups.Single().PendingActiveMessages == 0,
            "expired busy batch replied late after generation cleanup");
        return Task.CompletedTask;
    }

    private static Task AmbientDiscard()
    {
        var policy = new GroupChatPolicy(Options(GroupChatMode.Active) with
        { ActiveMessageEvery = 3, MentionInterval = TimeSpan.FromSeconds(3), ActiveInterval = TimeSpan.FromSeconds(3), ActivePerHour = 1 }, () => Now);
        var direct = policy.Decide(Message(1), Now);
        for (var id = 2; id <= 4; id++) policy.Decide(Message(id, Now.AddSeconds(id), mentioned: false), Now.AddSeconds(id));
        policy.SetPaused(true);
        Assert(policy.Snapshot(Now.AddSeconds(4)).Groups.Single() is { PendingActiveMessages: 0, ActiveTriggerMessageId: null, ActiveDueAt: null } &&
            policy.TakeReadyActiveReplies(Now.AddSeconds(4)).Count == 0, "pause retained a complete ordinary batch");
        policy.Complete(direct.ReservationId!.Value, false, Now.AddSeconds(4));
        policy.SetPaused(false);
        Assert(policy.TakeReadyActiveReplies(Now.AddSeconds(4)).Count == 0, "resume replayed discarded ordinary work");
        GroupChatDecision? active = null;
        for (var id = 5; id <= 7; id++) active = policy.Decide(Message(id, Now.AddSeconds(id), mentioned: false), Now.AddSeconds(id));
        Assert(active is { ShouldReply: true, Trigger: GroupChatTrigger.Ambient }, "fresh post-resume batch did not reach its threshold");
        policy.Complete(active!.ReservationId!.Value, false, Now.AddSeconds(7));
        for (var id = 8; id <= 12; id++) Assert(policy.Decide(Message(id, Now.AddSeconds(id), mentioned: false), Now.AddSeconds(id)).Reason == "hourly_limit",
            "exhausted ambient budget kept collecting deferred work");
        Assert(policy.Snapshot(Now.AddSeconds(12)).Groups.Single().PendingActiveMessages == 0 &&
            policy.TakeReadyActiveReplies(Now.AddSeconds(3607)).Count == 0 &&
            policy.Snapshot(Now.AddSeconds(3607)).Groups.Single().ActiveReservedLastHour == 0,
            "hourly reset replayed an exhausted-period ordinary backlog");
        return Task.CompletedTask;
    }

    private static Task AmbientSamplingIsolation()
    {
        var policy = new GroupChatPolicy(Options(GroupChatMode.Active) with { ActiveMessageEvery = 3 }, () => Now);
        foreach (var (id, group, seconds) in new[] { (1, 10, 0), (1, 11, 1), (2, 10, 2), (2, 11, 3) })
            Assert(policy.Decide(Message(id, Now.AddSeconds(seconds), group, mentioned: false, text: "好困"), Now.AddSeconds(seconds)).Reason == "active_waiting",
                "ordinary sample counters leaked across groups");
        Assert(policy.Snapshot(Now.AddSeconds(3)).Groups.All(group => group.ActiveMessagesSinceOpportunity == 2 && group.ReservedLastHour == 0),
            "sampling counted messages as API reservations");
        policy.SetPaused(true);
        Assert(policy.Decide(Message(3, Now.AddSeconds(4), mentioned: false, text: "回家啦"), Now.AddSeconds(4)).Reason == "paused" &&
            policy.Snapshot(Now.AddSeconds(4)).Groups.All(group => group.ActiveMessagesSinceOpportunity == 0),
            "paused group retained a partial opportunity or counted new text");
        policy.SetPaused(false);
        Assert(policy.Decide(Message(4, Now.AddSeconds(5), mentioned: false, text: "回家啦"), Now.AddSeconds(5)).Reason == "active_waiting" &&
            policy.Decide(Message(3, Now.AddSeconds(6), group: 11, mentioned: false, text: "好困"), Now.AddSeconds(6)).Reason == "active_waiting",
            "resume replayed an old partial sample");
        Assert(policy.Decide(Message(4, Now.AddSeconds(5), mentioned: false, text: "回家啦"), Now.AddSeconds(7)).Reason == "duplicate",
            "duplicate sample survived pause/resume deduplication");
        Assert(policy.Decide(Message(5, Now.AddSeconds(8), mentioned: false, text: "好困"), Now.AddSeconds(8)).Reason == "active_waiting",
            "group reached an ambient opportunity too soon after pause");
        var next = policy.Decide(Message(6, Now.AddSeconds(9), mentioned: false, text: "想睡觉"), Now.AddSeconds(9));
        Assert(next.ShouldReply, "fresh post-resume ordinary text never reached its own sampling boundary");
        policy.Complete(next.ReservationId!.Value, false, Now.AddSeconds(9));
        policy.RecordOwnReply(10, "群里的机器人回复", Now.AddSeconds(10));
        var status = policy.Snapshot(Now.AddSeconds(10)).Groups;
        Assert(status.Single(group => group.GroupId == 10).ActiveMessagesSinceOpportunity == 0 &&
            status.Single(group => group.GroupId == 11).ActiveMessagesSinceOpportunity == 1,
            "own replies or a different group incremented the ambient counter");
        return Task.CompletedTask;
    }

    private static Task ReservationLifecycle()
    {
        var policy = Policy(); var first = policy.Decide(Message(1), Now);
        var firstId = first.ReservationId!.Value;
        Assert(policy.IsCurrent(firstId, Now.AddSeconds(59)) && !policy.IsCurrent(firstId, Now.AddSeconds(60)), "reservation expiry boundary wrong");
        var expired = policy.Snapshot(Now.AddSeconds(61)).Groups.Single();
        Assert(expired.Busy && !expired.ReservationCurrent, "expiry automatically released busy");
        Assert(policy.Decide(Message(2, Now.AddSeconds(61)), Now.AddSeconds(61)).Reason == "busy", "expired old worker overlapped new reservation");
        Assert(policy.Complete(firstId, false, Now.AddSeconds(61)), "matching expired completion could not release");
        var second = policy.Decide(Message(3, Now.AddSeconds(62)), Now.AddSeconds(62));
        Assert(second.ShouldReply && !policy.Complete(firstId, true, Now.AddSeconds(62)) && policy.IsCurrent(second.ReservationId!.Value, Now.AddSeconds(62)),
            "late completion released a newer reservation");
        policy.SetPaused(true); policy.SetPaused(false);
        Assert(!policy.IsCurrent(second.ReservationId!.Value, Now.AddSeconds(63)) && policy.Snapshot(Now.AddSeconds(63)).Groups.Single().Busy,
            "unpause resurrected invalidated work");
        policy.Complete(second.ReservationId!.Value, false, Now.AddSeconds(63));
        policy.SetPaused(true);
        var pausedMessage = Message(4, Now.AddSeconds(72));
        Assert(policy.Decide(pausedMessage, Now.AddSeconds(72)).Reason == "paused", "pause failed");
        policy.SetPaused(false);
        Assert(policy.Decide(pausedMessage, Now.AddSeconds(73)).Reason == "duplicate", "paused work was queued for resume");
        return Task.CompletedTask;
    }

    private static Task EventCapacity()
    {
        var policy = Policy(GroupChatMode.Manual);
        for (var id = 0; id < GroupChatPolicy.MaximumSeenEvents; id++) policy.Decide(Message(id), Now);
        Assert(policy.Snapshot(Now).SeenEvents == 2048 && policy.Decide(Message(0), Now).Reason == "duplicate", "fresh dedup was evicted");
        Assert(policy.Decide(Message(3000), Now).Reason == "event_capacity", "dedup capacity failed open");
        var later = Now.AddMinutes(11);
        policy.Decide(Message(4000, later), later);
        Assert(policy.Snapshot(later).SeenEvents == 1 && policy.Decide(Message(0), later).Reason == "event_time_out_of_range", "TTL did not prune or stale replay accepted");
        return Task.CompletedTask;
    }

    private static Task ContextBounds()
    {
        var policy = Policy(GroupChatMode.Manual);
        for (var i = 0; i < 50; i++) policy.Decide(Message(i, text: new string('文', 500)), Now);
        var status = policy.Snapshot(Now).Groups.Single();
        Assert(status.ContextCount <= 20 && status.ContextCharacters <= 4000, "context grew beyond bounds");
        Assert(policy.Decide(Message(90, text: new string('文', 1001)), Now).Reason == "invalid_text", "oversized parsed text accepted");
        var text = new string('文', 999) + "😀";
        Assert(policy.TryParse(Event(new object[] { new { type = "text", data = new { text } } }), out var parsed, out _, Now) &&
            parsed!.Text.Length <= 1000 && !char.IsHighSurrogate(parsed.Text[^1]), "text truncation split a surrogate");
        var huge = Event(new object[] { new { type = "text", data = new { text = new string('中', 25000) } } });
        Assert(!policy.TryParse(huge, out _, out _, Now), "byte cap ignored multibyte payload");
        return Task.CompletedTask;
    }

    private static Task ClockRollback()
    {
        var time = Now; var policy = new GroupChatPolicy(Options(), () => time);
        var first = policy.Decide(Message(1)); policy.Complete(first.ReservationId!.Value, true);
        time = Now.AddSeconds(8); var second = policy.Decide(Message(2, time)); policy.Complete(second.ReservationId!.Value, true);
        time = Now;
        Assert(policy.Decide(Message(3)).Reason == "cooldown", "clock rollback reset cooldown");
        return Task.CompletedTask;
    }

    private static Task OwnReplyContext()
    {
        var policy = Policy();
        for (var i = 0; i < 25; i++) policy.RecordOwnReply(10, new string('答', 1500), Now);
        var status = policy.Snapshot(Now);
        Assert(status.SeenEvents == 0 && !status.Groups.Single().Busy && status.Groups.Single().ReservedLastHour == 0 &&
            status.Groups.Single().ContextCount <= 20 && status.Groups.Single().ContextCharacters <= 4000,
            "own reply created work, consumed dedup, or exceeded context bounds");
        var decision = policy.Decide(Message(1), Now);
        var own = decision.Context.Where(message => message.SenderId == 99).ToArray();
        Assert(decision.ShouldReply && own.Length > 0 && own.All(message => message.MessageId == 0 && message.Text.Length <= OneBotMessageClient.MaximumTextCharacters + 80),
            "next prompt missed acknowledged own reply or clipping");
        Assert(policy.Decide(own[0], Now).Reason == "invalid_or_self_sender", "own context fed a reply loop");
        try { policy.RecordOwnReply(12, "越界"); throw new Exception("own context escaped group allowlist"); }
        catch (ArgumentException) { }
        return Task.CompletedTask;
    }

    private static Task Configuration()
    {
        foreach (var invalid in new[]
        {
            Options() with { SelfId = 0 }, Options() with { AllowedGroupIds = Array.Empty<long>() },
            Options() with { MentionInterval = TimeSpan.FromMilliseconds(2999) }, Options() with { ActiveInterval = TimeSpan.FromMilliseconds(2999) },
            Options() with { PrivateInterval = TimeSpan.FromMilliseconds(2999) },
            Options() with { MentionInterval = TimeSpan.FromSeconds(3601) }, Options() with { ActiveInterval = TimeSpan.FromSeconds(3601) },
            Options() with { PrivateInterval = TimeSpan.FromSeconds(3601) },
            Options() with { MentionPerHour = 121 }, Options() with { ActivePerHour = 121 }, Options() with { PrivatePerHour = 121 },
            Options() with { MentionPerHour = 0 }, Options() with { ActivePerHour = 0 }, Options() with { PrivatePerHour = 0 },
            Options() with { ActiveMessageEvery = 0 }, Options() with { ActiveMessageEvery = 21 },
            Options() with { EventTtl = TimeSpan.FromMinutes(1) }, Options() with { ReservationTimeout = TimeSpan.FromSeconds(151) }
        })
        {
            try { _ = new GroupChatPolicy(invalid); throw new Exception("invalid settings accepted"); }
            catch (ArgumentException) { }
        }
        var groups = new List<long> { 10 }; var names = new[] { "小深" };
        _ = new GroupChatPolicy(Options() with { ActiveMessageEvery = 20 });
        var longPolicy = new GroupChatPolicy(Options() with { ReservationTimeout = TimeSpan.FromSeconds(150) }, () => Now);
        var longReply = longPolicy.Decide(Message(900), Now);
        var longId = longReply.ReservationId ?? throw new Exception("long reply not reserved");
        Assert(longReply.ShouldReply && longPolicy.IsCurrent(longId, Now.AddSeconds(120)) &&
            !longPolicy.IsCurrent(longId, Now.AddSeconds(150)), "long reply lease expired before its provider or outlived its bound");
        longPolicy.Complete(longId, false, Now.AddSeconds(150));
        var policy = new GroupChatPolicy(Options() with { AllowedGroupIds = groups, BotNames = names }, () => Now);
        groups.Add(12); names[0] = "别名";
        Assert(policy.Decide(Message(1, group: 12), Now).Reason == "group_not_allowed" &&
            policy.Decide(Message(2, mentioned: false, text: "小深，问题"), Now).ShouldReply, "external settings mutation changed policy scope");
        return Task.CompletedTask;
    }

    private static Task ConfigurableRateBoundaries()
    {
        foreach (var addressed in new[] { true, false })
        foreach (var limit in new[] { 60, 120 })
        {
            var policy = new GroupChatPolicy(Options(GroupChatMode.Active) with
            {
                MentionInterval = TimeSpan.FromSeconds(3), ActiveInterval = TimeSpan.FromSeconds(3),
                MentionPerHour = limit, ActivePerHour = limit
            }, () => Now);
            for (var index = 0; index < limit; index++)
            {
                var at = Now.AddSeconds(index * 3);
                var decision = policy.Decide(Message(index + 1, at, mentioned: addressed), at);
                Assert(decision.ShouldReply, "configured group route still imposed an old interval or hourly cap");
                policy.Complete(decision.ReservationId!.Value, false, at);
                if (index == 0)
                {
                    var early = Now.AddMilliseconds(2999);
                    Assert(policy.Decide(Message(1000, early, mentioned: addressed), early).Reason == "cooldown",
                        "configured three-second group interval admitted an event one millisecond early");
                }
            }
            var exhausted = Now.AddSeconds(limit * 3);
            Assert(policy.Decide(Message(1001, exhausted, mentioned: addressed), exhausted).Reason == "hourly_limit",
                "configured group cap admitted an extra attempt");
            var beforeRenewal = Now.AddSeconds(3599);
            Assert(policy.Decide(Message(1002, beforeRenewal, mentioned: addressed), beforeRenewal).Reason == "hourly_limit",
                "configured group allowance expired before its rolling hour");
            var renewal = Now.AddHours(1);
            var next = policy.Decide(Message(1003, renewal, mentioned: addressed), renewal);
            Assert(next.ShouldReply, "configured group allowance failed to renew at the exact rolling boundary");
            policy.Complete(next.ReservationId!.Value, false, renewal);
            var status = policy.Snapshot(renewal).Groups.Single();
            Assert(status.ReservedLastHour == limit &&
                (addressed ? status.DirectReservedLastHour == limit && status.ActiveReservedLastHour == 0
                    : status.ActiveReservedLastHour == limit && status.DirectReservedLastHour == 0),
                "configured rate changed the independent attempt counters");
        }
        return Task.CompletedTask;
    }

    private static Task SettingsRateBoundaries()
    {
        var settings = new GroupChatSettings { AllowedGroupIds = [10], SelfId = 99, AllowedPrivateUserIds = [20] };
        settings.Validate();
        Assert(settings.PrivateIntervalSeconds == 8 && settings.PrivatePerHour == 30 &&
            settings.MentionIntervalSeconds == 8 && settings.MentionPerHour == 30 &&
            settings.ActiveIntervalSeconds == 120 && settings.ActivePerHour == 6 && settings.ActiveMessageEvery == 3,
            "allowing configurable rates unexpectedly increased the default reply frequency");
        foreach (var interval in new[] { 3, 3600 })
        foreach (var limit in new[] { 1, 60, 120 })
        foreach (var every in new[] { 1, 20 })
            (settings with
            {
                PrivateIntervalSeconds = interval, MentionIntervalSeconds = interval, ActiveIntervalSeconds = interval,
                PrivatePerHour = limit, MentionPerHour = limit, ActivePerHour = limit, ActiveMessageEvery = every
            }).Validate();
        foreach (var invalid in new[]
        {
            settings with { PrivateIntervalSeconds = 2 }, settings with { MentionIntervalSeconds = 2 }, settings with { ActiveIntervalSeconds = 2 },
            settings with { PrivateIntervalSeconds = 3601 }, settings with { MentionIntervalSeconds = 3601 }, settings with { ActiveIntervalSeconds = 3601 },
            settings with { PrivatePerHour = 0 }, settings with { MentionPerHour = 0 }, settings with { ActivePerHour = 0 },
            settings with { PrivatePerHour = 121 }, settings with { MentionPerHour = 121 }, settings with { ActivePerHour = 121 },
            settings with { ActiveMessageEvery = 0 }, settings with { ActiveMessageEvery = 21 }
        })
        {
            try { invalid.Validate(); throw new Exception("out-of-range persisted rate was accepted"); }
            catch (ArgumentException) { }
        }
        return Task.CompletedTask;
    }
}
