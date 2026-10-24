using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AudioBridge;

public static class PrivateChatChecks
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private const string ReplyJson = "{\"send\":true,\"text\":\"新的私聊回答\",\"memeId\":null}";

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("incoming private identity and whitelist filters reject unsafe events", ParseFilters),
            ("private messages trigger without mentions and use private prompt context", DirectTrigger),
            ("contacts and same-number group isolate history, deduplication and reservations", ScopeIsolation),
            ("each private contact has independent eight-second and rolling thirty-attempt limits", RateAndDeduplication),
            ("configured three-second private bursts honor sixty and 120 per contact each rolling hour", ConfigurableRateBoundaries),
            ("private bursts merge once after busy and cooldown with contact isolation", PendingBurst),
            ("paused and expired private pending messages are discarded without replay", PendingCleanup),
            ("private pending capacity and hourly limits stay bounded without delayed spend", PendingBounds),
            ("private send acknowledgement alone records that contact's own reply", DeliveryAndHistory),
            ("3000-character private reply preserves send, confirmed history and local archive", LongReply),
            ("cancelled uncooperative private model cannot send after the next reply", CancelLateReply),
            ("unauthorized private events never invoke the model or message API", UnauthorizedPipeline)
        };
        foreach (var check in checks)
        {
            await check.Run().WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine("PASS private chat: " + check.Name);
        }
        return checks.Length;
    }

    private static GroupChatPolicyOptions Options() => new()
    {
        SelfId = 99, AllowedGroupIds = [20], AllowedPrivateUserIds = [20, 21], BotNames = ["机器人"]
    };
    private static GroupChatPolicy Policy() => new(Options(), () => Now);
    private static ParsedGroupMessage Message(long id, long sender = 20, DateTimeOffset? at = null, string text = "你好") =>
        new(0, sender, id, at ?? Now, text, false, false, IsPrivate: true);
    private static JsonElement Event(long user = 20) => JsonSerializer.SerializeToElement(new
    {
        post_type = "message", message_type = "private", sub_type = "friend", self_id = 99,
        user_id = user, sender = new { user_id = user }, message_id = -7, time = Now.ToUnixTimeSeconds(),
        message = new[] { new { type = "text", data = new { text = "你好" } } }
    });
    private static JsonElement Change(JsonElement value, string property, object replacement)
    {
        var node = JsonNode.Parse(value.GetRawText())!.AsObject();
        node[property] = JsonSerializer.SerializeToNode(replacement);
        return JsonSerializer.SerializeToElement(node);
    }

    private static Task ParseFilters()
    {
        var policy = Policy();
        Check(policy.TryParse(Event(), out var parsed, out _, Now) &&
            parsed is { IsPrivate: true, GroupId: 0, SenderId: 20, Mentioned: false }, "valid private event was rejected or treated as a group");
        foreach (var invalid in new[]
        {
            Event(22), Event(99), Change(Event(), "self_id", 98), Change(Event(), "sender", new { user_id = 21 }),
            Change(Event(), "sub_type", "self_sent"), Change(Event(), "post_type", "message_sent"),
            Change(Event(), "time", Now.AddMinutes(-3).ToUnixTimeSeconds())
        })
            Check(!policy.TryParse(invalid, out _, out _, Now), "unauthorized or self private event parsed");
        var noPrivate = new GroupChatPolicy(Options() with { AllowedPrivateUserIds = [] }, () => Now);
        Check(!noPrivate.TryParse(Event(), out _, out _, Now), "private scope defaulted open");
        Check(!policy.Decide(Message(1, sender: 22), Now).ShouldReply &&
            !policy.Decide(Message(2, sender: 99), Now).ShouldReply && policy.Snapshot(Now).SeenEvents == 0,
            "direct parsed-message path bypassed private scope or polluted deduplication");
        return Task.CompletedTask;
    }

    private static async Task DirectTrigger()
    {
        var policy = Policy();
        Check(policy.TryParse(Event(), out var parsed, out _, Now), "private parse failed");
        var decision = policy.Decide(parsed!, Now);
        Check(decision.ShouldReply && !decision.Message.Mentioned, "private greeting required an @ or a bot name");
        var capture = new CaptureReply();
        var brain = new GroupChatBrain(_ => capture, [], 99);
        var reply = await brain.GenerateAsync(decision.Context, default, decision.Message);
        Check(reply.Send && capture.History.Single().Content.Contains("私聊", StringComparison.Ordinal),
            "private reply reused a group-only prompt");
        policy.Complete(decision.ReservationId!.Value, true, Now);
    }

    private static Task ScopeIsolation()
    {
        var policy = Policy();
        var a = policy.Decide(Message(7, text: "甲的私密问题"), Now);
        var b = policy.Decide(Message(7, sender: 21, text: "乙的问题"), Now);
        var group = policy.Decide(new(20, 20, 7, Now, "群里的问题", true, false), Now);
        Check(a.ShouldReply && b.ShouldReply && group.ShouldReply,
            "same message id or active reservation crossed contact/group boundaries");
        Check(a.Context.All(m => m.IsPrivate && m.SenderId == 20) &&
            b.Context.All(m => m.IsPrivate && m.SenderId == 21) && group.Context.All(m => !m.IsPrivate),
            "initial prompts leaked another scope's history");
        foreach (var item in new[] { a, b, group }) policy.Complete(item.ReservationId!.Value, false, Now);
        policy.RecordOwnPrivateReply(20, "甲的私密回答", Now);
        policy.RecordOwnPrivateReply(21, "乙的回答", Now);
        policy.RecordOwnReply(20, "群里的回答", Now);
        var later = Now.AddSeconds(8);
        var aNext = policy.Decide(Message(8, at: later), later);
        var bNext = policy.Decide(Message(8, sender: 21, at: later), later);
        var groupNext = policy.Decide(new(20, 20, 8, later, "群里追问", true, false), later);
        Check(aNext.Context.Any(m => m.Text == "甲的私密回答") &&
            aNext.Context.All(m => !m.Text.Contains("乙的", StringComparison.Ordinal) && !m.Text.Contains("群里", StringComparison.Ordinal)) &&
            bNext.Context.Any(m => m.Text == "乙的回答") &&
            bNext.Context.All(m => !m.Text.Contains("甲的", StringComparison.Ordinal) && !m.Text.Contains("群里", StringComparison.Ordinal)) &&
            groupNext.Context.Any(m => m.Text == "群里的回答") && groupNext.Context.All(m => !m.IsPrivate),
            "acknowledged own replies leaked across private contacts or into the group");
        var status = policy.Snapshot(later);
        Check(status.PrivateContacts.Count == 2 && status.Groups.Count == 1 &&
            status.PrivateContacts.All(c => c.Busy && c.ReservationCurrent && c.ContextCount == 3 && c.ReservedLastHour == 2),
            "private status merged contacts or omitted reservations/context");
        foreach (var item in new[] { aNext, bNext, groupNext }) policy.Complete(item.ReservationId!.Value, false, later);
        var rejected = false;
        try { policy.RecordOwnPrivateReply(22, "越界", later); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "own private reply escaped whitelist");
        return Task.CompletedTask;
    }

    private static Task RateAndDeduplication()
    {
        var policy = Policy();
        var first = policy.Decide(Message(1), Now);
        Check(first.ShouldReply, "first private message refused");
        policy.Complete(first.ReservationId!.Value, false, Now);
        Check(policy.Decide(Message(1), Now).Reason == "duplicate", "private duplicate consumed a second attempt");
        var early = Now.AddSeconds(7);
        var cooldownPolicy = Policy();
        var cooldownFirst = cooldownPolicy.Decide(Message(1), Now);
        cooldownPolicy.Complete(cooldownFirst.ReservationId!.Value, false, Now);
        var waiting = cooldownPolicy.Decide(Message(99, at: early), early);
        Check(!waiting.ShouldReply && waiting.Reason == "private_pending" &&
            cooldownPolicy.TakeReadyPrivateReplies(early).Count == 0 &&
            cooldownPolicy.Snapshot(early).PrivateContacts.Single().ReservedLastHour == 1,
            "private interval sent or charged a seventh-second message before it became ready");
        for (var index = 1; index < 30; index++)
        {
            var at = Now.AddSeconds(index * 8);
            var next = policy.Decide(Message(index + 1, at: at), at);
            Check(next.ShouldReply, "private cooldown rejection extended cooldown or thirty-attempt cap fired early");
            policy.Complete(next.ReservationId!.Value, false, at);
        }
        var exhausted = Now.AddSeconds(240);
        Check(policy.Decide(Message(100, at: exhausted), exhausted).Reason == "hourly_limit",
            "private failures did not consume the thirty-attempt hourly allowance");
        var other = policy.Decide(Message(1, sender: 21, at: exhausted), exhausted);
        var group = policy.Decide(new(20, 20, 1, exhausted, "群里提问", true, false), exhausted);
        Check(other.ShouldReply && group.ShouldReply, "one contact exhausted another contact or the same-number group");
        policy.Complete(other.ReservationId!.Value, false, exhausted);
        policy.Complete(group.ReservationId!.Value, false, exhausted);
        var before = Now.AddSeconds(3599);
        Check(policy.Decide(Message(101, at: before), before).Reason == "hourly_limit", "hourly attempts expired early");
        var boundary = Now.AddHours(1);
        var renewed = policy.Decide(Message(102, at: boundary), boundary);
        Check(renewed.ShouldReply && policy.Snapshot(boundary).PrivateContacts.Single(c => c.UserId == 20).ReservedLastHour == 30,
            "private rolling-hour boundary reset too many attempts or failed to renew");
        policy.Complete(renewed.ReservationId!.Value, false, boundary);
        Check(policy.Decide(Message(99, at: early), boundary).Reason == "event_time_out_of_range", "old cooled-down message was replayed later");
        return Task.CompletedTask;
    }

    private static Task ConfigurableRateBoundaries()
    {
        foreach (var limit in new[] { 60, 120 })
        {
            var policy = new GroupChatPolicy(Options() with
            {
                PrivateInterval = TimeSpan.FromSeconds(3), PrivatePerHour = limit
            }, () => Now);
            var first = policy.Decide(Message(1), Now);
            Check(first.ShouldReply, "configured private route rejected its first request");
            policy.Complete(first.ReservationId!.Value, false, Now);
            var early = Now.AddMilliseconds(2999);
            var pending = Message(2, at: early, text: "保留的快速追问");
            Check(policy.Decide(pending, early).Reason == "private_pending" &&
                policy.TakeReadyPrivateReplies(early).Count == 0,
                "configured private burst bypassed the exact three-second interval");
            var ready = policy.TakeReadyPrivateReplies(Now.AddSeconds(3));
            Check(ready.Count == 1 && ready[0].Message.Text == pending.Text &&
                policy.TakeReadyPrivateReplies(Now.AddSeconds(3)).Count == 0,
                "three-second private burst was lost or drained twice");
            policy.Complete(ready[0].ReservationId!.Value, false, Now.AddSeconds(3));
            for (var index = 2; index < limit; index++)
            {
                var at = Now.AddSeconds(index * 3);
                var next = policy.Decide(Message(index + 1, at: at), at);
                Check(next.ShouldReply, "configured private interval or hourly cap still used the old minimum");
                policy.Complete(next.ReservationId!.Value, false, at);
            }
            var exhausted = Now.AddSeconds(limit * 3);
            Check(policy.Decide(Message(1000, at: exhausted), exhausted).Reason == "hourly_limit" &&
                policy.Snapshot(exhausted).PrivateContacts.Single().PendingMessages == 0,
                "configured private hourly cap admitted or queued an extra request");
            var other = policy.Decide(Message(1000, sender: 21, at: exhausted), exhausted);
            Check(other.ShouldReply && other.Context.All(item => item.SenderId == 21),
                "configured private allowance or context crossed contacts");
            policy.Complete(other.ReservationId!.Value, false, exhausted);
            var beforeRenewal = Now.AddSeconds(3599);
            Check(policy.Decide(Message(1001, at: beforeRenewal), beforeRenewal).Reason == "hourly_limit",
                "configured private allowance expired before the rolling hour");
            var renewal = Now.AddHours(1);
            var renewed = policy.Decide(Message(1002, at: renewal), renewal);
            Check(renewed.ShouldReply && policy.Snapshot(renewal).PrivateContacts.Single(item => item.UserId == 20).ReservedLastHour == limit,
                "configured private rolling renewal reset too many attempts or rejected the exact boundary");
            policy.Complete(renewed.ReservationId!.Value, false, renewal);
        }
        return Task.CompletedTask;
    }

    private static Task PendingBurst()
    {
        var policy = Policy();
        var first = policy.Decide(Message(1, text: "甲的第一句"), Now);
        Check(first.ShouldReply, "first private burst message did not start immediately");
        var second = Message(2, at: Now.AddSeconds(1), text: "甲的第二句");
        var third = Message(3, at: Now.AddSeconds(2), text: "甲的第三句");
        Check(policy.Decide(second, second.At).Reason == "private_pending" &&
            policy.Decide(third, third.At).Reason == "private_pending", "busy private burst dropped a follow-up");
        var otherFirst = policy.Decide(Message(1, sender: 21, at: Now.AddSeconds(2), text: "乙的第一句"), Now.AddSeconds(2));
        var otherPending = Message(2, sender: 21, at: Now.AddSeconds(3), text: "乙的第二句");
        Check(otherFirst.ShouldReply && policy.Decide(otherPending, otherPending.At).Reason == "private_pending",
            "another contact could not independently start and retain a follow-up");
        Check(policy.Decide(second, Now.AddSeconds(3)).Reason == "duplicate", "duplicate private message entered pending twice");
        var pending = policy.Snapshot(Now.AddSeconds(3)).PrivateContacts;
        Check(pending.Single(c => c.UserId == 20).PendingMessages == 2 &&
            pending.Single(c => c.UserId == 21).PendingMessages == 1 && pending.All(c => c.ReservedLastHour == 1),
            "pending duplicates consumed model attempts or contact queues merged");
        Check(policy.TakeReadyPrivateReplies(Now.AddSeconds(7)).Count == 0,
            "pending message reserved while the previous model was still busy");
        policy.Complete(first.ReservationId!.Value, false, Now.AddSeconds(7));
        Check(policy.TakeReadyPrivateReplies(Now.AddMilliseconds(7999)).Count == 0,
            "completion bypassed the eight-second private interval");
        var ready = policy.TakeReadyPrivateReplies(Now.AddSeconds(8));
        Check(ready.Count == 1 && ready[0].ShouldReply && ready[0].Message is { IsPrivate: true, SenderId: 20, MessageId: 3 } &&
            ready[0].Message.Text == "甲的第二句\n甲的第三句" &&
            ready[0].Context.Count(m => m.Text == second.Text || m.Text == third.Text) == 2 &&
            ready[0].Context.All(m => !m.Text.StartsWith("乙的", StringComparison.Ordinal)),
            "ready private batch omitted burst messages, repeated history, or mixed another contact");
        Check(policy.TakeReadyPrivateReplies(Now.AddSeconds(8)).Count == 0 &&
            policy.Snapshot(Now.AddSeconds(8)).PrivateContacts.Single(c => c.UserId == 20) is
                { PendingMessages: 0, ReservedLastHour: 2 },
            "repeated drain reserved the same batch or charged every burst message separately");
        policy.Complete(ready[0].ReservationId!.Value, false, Now.AddSeconds(8));
        policy.Complete(otherFirst.ReservationId!.Value, false, Now.AddSeconds(8));
        Check(policy.TakeReadyPrivateReplies(Now.AddSeconds(9)).Count == 0,
            "another contact inherited the first contact's cooldown timestamp");
        var otherReady = policy.TakeReadyPrivateReplies(Now.AddSeconds(10));
        Check(otherReady.Count == 1 && otherReady[0].Message.SenderId == 21 && otherReady[0].Message.Text == "乙的第二句" &&
            otherReady[0].Context.All(m => !m.Text.StartsWith("甲的", StringComparison.Ordinal)),
            "second contact's private batch was lost or contaminated");
        policy.Complete(otherReady[0].ReservationId!.Value, false, Now.AddSeconds(10));
        Check(policy.TakeReadyPrivateReplies(Now.AddSeconds(18)).Count == 0 &&
            policy.Decide(second, Now.AddSeconds(18)).Reason == "duplicate",
            "drained batch replayed or stopped deduplicating its original message IDs");
        return Task.CompletedTask;
    }

    private static Task PendingCleanup()
    {
        foreach (var pause in new[] { true, false })
        {
            var policy = Policy();
            var first = policy.Decide(Message(1), Now);
            var pending = Message(2, at: Now.AddSeconds(1), text: "稍后处理的追问");
            Check(policy.Decide(pending, pending.At).Reason == "private_pending", "follow-up was not retained before cleanup");
            policy.Complete(first.ReservationId!.Value, false, Now.AddSeconds(2));
            if (pause) { policy.SetPaused(true); policy.SetPaused(false); }
            var after = pause ? Now.AddSeconds(8) : Now.AddSeconds(122);
            Check(policy.TakeReadyPrivateReplies(after).Count == 0 &&
                policy.Snapshot(after).PrivateContacts.Single() is { PendingMessages: 0, ReservedLastHour: 1 },
                pause ? "resume replayed a paused private follow-up" : "expired private follow-up reserved a model request");
            Check(policy.Decide(pending, after).Reason == (pause ? "duplicate" : "event_time_out_of_range"),
                "cleanup made a delayed original event eligible again");
            var fresh = policy.Decide(Message(3, at: after, text: "现在的新问题"), after);
            Check(fresh.ShouldReply, "discarded private follow-up left a stale busy flag or blocked new work");
            policy.Complete(fresh.ReservationId!.Value, false, after);
        }
        return Task.CompletedTask;
    }

    private static Task PendingBounds()
    {
        foreach (var characterBound in new[] { false, true })
        {
            var policy = Policy();
            var first = policy.Decide(Message(1), Now);
            var count = characterBound ? 3 : 20;
            var texts = Enumerable.Range(0, count).Select(i => characterBound ? new string((char)('甲' + i), 1000) : "待答" + i).ToArray();
            for (var index = 0; index < count; index++)
                Check(policy.Decide(Message(index + 2, at: Now.AddSeconds(1), text: texts[index]), Now.AddSeconds(1)).Reason == "private_pending",
                    "pending limit rejected a bounded private batch too early");
            var overflow = Message(100, at: Now.AddSeconds(1), text: characterBound ? new string('溢', 1000) : "不应进入的第21句");
            Check(policy.Decide(overflow, overflow.At).Reason == "private_pending_capacity" &&
                policy.Snapshot(overflow.At).PrivateContacts.Single().PendingMessages == count,
                "private pending exceeded the message or joined-character bound");
            Check(policy.Decide(overflow, overflow.At).Reason == "duplicate", "capacity rejection could be retried as fresh input");
            policy.Complete(first.ReservationId!.Value, false, Now.AddSeconds(8));
            var ready = policy.TakeReadyPrivateReplies(Now.AddSeconds(8));
            Check(ready.Count == 1 && ready[0].Message.Text == string.Join("\n", texts) && ready[0].Message.Text.Length <= 4000,
                "bounded private drain included rejected text or discarded admitted pending messages");
            policy.Complete(ready[0].ReservationId!.Value, false, Now.AddSeconds(8));
        }
        var exhaustedPolicy = Policy();
        GroupChatDecision? last = null;
        for (var index = 0; index < 30; index++)
        {
            var at = Now.AddSeconds(index * 8);
            last = exhaustedPolicy.Decide(Message(index + 1, at: at), at);
            Check(last.ShouldReply, "hourly allowance exhausted before thirty requests");
            if (index < 29) exhaustedPolicy.Complete(last.ReservationId!.Value, false, at);
        }
        var blockedAt = Now.AddSeconds(233);
        var blocked = exhaustedPolicy.Decide(Message(100, at: blockedAt), blockedAt);
        Check(!blocked.ShouldReply && blocked.Reason == "hourly_limit" &&
            exhaustedPolicy.Snapshot(blockedAt).PrivateContacts.Single().PendingMessages == 0,
            "hourly-exhausted busy contact accumulated a deferred model request");
        exhaustedPolicy.Complete(last!.ReservationId!.Value, false, blockedAt);
        Check(exhaustedPolicy.TakeReadyPrivateReplies(Now.AddHours(1)).Count == 0,
            "hourly limit queued an old message to spend automatically in the next hour");
        return Task.CompletedTask;
    }

    private static async Task DeliveryAndHistory()
    {
        var directory = MakeLibrary();
        try
        {
            foreach (var live in new[] { false, true })
            {
                var policy = Policy();
                var handler = new PrivateApi();
                using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["20"], handler, allowedPrivateUsers: ["20", "21"]);
                await using var responder = new GroupChatResponder(policy, new GroupChatBrain(_ => new FixedReply(ReplyJson), [], 99),
                    new MemeLibrary(directory), client, live, 99);
                var result = await responder.HandleAsync(policy.Decide(Message(1), Now), default);
                var status = policy.Snapshot(Now);
                Check(result.Status == (live ? "api_accepted" : "preview") && handler.PrivateSends == (live ? 1 : 0) &&
                    status.PrivateContacts.Single().ContextCount == (live ? 2 : 1) && status.Groups.Count == 0,
                    "private result routed to a group, preview sent, or unacknowledged reply entered history");
            }
            var uncertainPolicy = Policy();
            var uncertainHandler = new PrivateApi { Confirm = false };
            using var uncertainClient = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["20"], uncertainHandler, allowedPrivateUsers: ["20"]);
            await using var uncertainResponder = new GroupChatResponder(uncertainPolicy,
                new GroupChatBrain(_ => new FixedReply(ReplyJson), [], 99), new MemeLibrary(directory), uncertainClient, true, 99);
            var failed = false;
            try { await uncertainResponder.HandleAsync(uncertainPolicy.Decide(Message(1), Now), default); }
            catch (OneBotMessageException error) { failed = error.DeliveryMayHaveOccurred; }
            Check(failed && uncertainHandler.PrivateSends == 1 && uncertainPolicy.Snapshot(Now).PrivateContacts.Single().ContextCount == 1,
                "uncertain private delivery retried or was recorded as heard");
        }
        finally { DeleteLibrary(directory); }
    }

    private static async Task LongReply()
    {
        var directory = MakeLibrary();
        try
        {
            var policy = Policy(); var handler = new PrivateApi();
            var text = new string('中', 3000);
            var contacts = new ContactContextStore(directory, () => Now);
            using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["20"], handler, allowedPrivateUsers: ["20"]);
            var brain = new GroupChatBrain(_ => new FixedReply(JsonSerializer.Serialize(new { send = true, text, memeId = "roger" })), ["roger"], 99,
                new PersonaSettings { ReplyMaxCharacters = 3000 });
            await using var responder = new GroupChatResponder(policy, brain, new MemeLibrary(directory), client, true, 99,
                (message, body, id) => contacts.AppendMessage(message.SenderId, "assistant", body, id));
            var result = await responder.HandleAsync(policy.Decide(Message(1)), default);
            Check(result.Status == "api_accepted" && handler.PrivateSends == 1 && handler.SentTexts.Single() == text,
                "3000-character body was truncated, rejected or sent more than once");
            Check(contacts.Recent(20).Single().Text == text, "long successful body lost from archive or meme marker exceeded storage bound");
            var next = policy.Decide(Message(2, at: Now.AddSeconds(8)), Now.AddSeconds(8));
            Check(next.Context.Any(item => item.SenderId == 99 && item.Text.StartsWith(text, StringComparison.Ordinal)), "long sent reply lost from bounded context");
            Check(policy.Snapshot(Now.AddSeconds(8)).PrivateContacts.Single().ContextCharacters <= 4000, "long reply removed context resource bounds");
            policy.Complete(next.ReservationId!.Value, false, Now.AddSeconds(8));
            try { contacts.AppendMessage(20, "assistant", text + "超", "bad"); throw new Exception("oversize archive accepted"); }
            catch (ArgumentException) { }
        }
        finally { DeleteLibrary(directory); }
    }

    private static async Task CancelLateReply()
    {
        var directory = MakeLibrary();
        var started = Ready(); var release = Ready(); var oldReturned = Ready();
        try
        {
            var time = DateTimeOffset.UtcNow;
            var policy = new GroupChatPolicy(Options(), () => time);
            var handler = new PrivateApi();
            using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["20"], handler, allowedPrivateUsers: ["20"]);
            var generations = 0;
            var brain = new GroupChatBrain(_ => Interlocked.Increment(ref generations) == 1
                ? new SlowReply(started, release, oldReturned) : new FixedReply(ReplyJson), [], 99);
            await using var responder = new GroupChatResponder(policy, brain, new MemeLibrary(directory), client, true, 99);
            var previous = responder.HandleAsync(policy.Decide(Message(1, at: time)), default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await responder.SetPausedAsync(true);
            var cancelled = false;
            try { await previous.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "cancelled private generation did not stop promptly");
            await responder.SetPausedAsync(false);
            time = time.AddSeconds(8);
            var newer = await responder.HandleAsync(policy.Decide(Message(2, at: time)), default);
            release.TrySetResult();
            await oldReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(newer.Status == "api_accepted" && handler.PrivateSends == 1 && handler.SentTexts.Single() == "新的私聊回答" &&
                !policy.Snapshot().PrivateContacts.Single().Busy, "late old private result leaked into the next send or left contact busy");
            var later = policy.Decide(Message(3, at: time.AddSeconds(8)), time.AddSeconds(8));
            Check(later.Context.Any(m => m.Text == "新的私聊回答") && later.Context.All(m => m.Text != "过期旧回答"),
                "cancelled model reply polluted future private context");
            policy.Complete(later.ReservationId!.Value, false, time.AddSeconds(8));
        }
        finally { release.TrySetResult(); DeleteLibrary(directory); }
    }

    private static async Task UnauthorizedPipeline()
    {
        var directory = MakeLibrary();
        try
        {
            var policy = Policy();
            var models = 0;
            var handler = new PrivateApi();
            using var client = new OneBotMessageClient("http://127.0.0.1:3000/", "fake", ["20"], handler, allowedPrivateUsers: ["20"]);
            await using var responder = new GroupChatResponder(policy,
                new GroupChatBrain(_ => { models++; return new FixedReply(ReplyJson); }, [], 99), new MemeLibrary(directory), client, true, 99);
            foreach (var json in new[] { Event(22), Event(99), Change(Event(), "self_id", 98) })
            {
                if (!policy.TryParse(json, out var parsed, out _, Now)) continue;
                var decision = policy.Decide(parsed!, Now);
                await responder.HandleAsync(decision, default);
            }
            Check(models == 0 && handler.Calls == 0 && policy.Snapshot(Now).PrivateContacts.Count == 0,
                "unauthorized private event entered model, HTTP or context storage");
        }
        finally { DeleteLibrary(directory); }
    }

    private sealed class CaptureReply : IStreamingReply
    {
        public IReadOnlyList<ChatTurn> History { get; private set; } = [];
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history, [EnumeratorCancellation] CancellationToken ct)
        { History = history; await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return ReplyJson; }
    }

    private sealed class SlowReply(TaskCompletionSource started, TaskCompletionSource release, TaskCompletionSource returned) : IStreamingReply
    {
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history, [EnumeratorCancellation] CancellationToken ct)
        {
            started.TrySetResult();
            await release.Task; // Deliberately ignores cancellation to test the late-result fence.
            try { yield return "{\"send\":true,\"text\":\"过期旧回答\",\"memeId\":null}"; }
            finally { returned.TrySetResult(); }
        }
    }

    private sealed class PrivateApi : HttpMessageHandler
    {
        public int Calls;
        public int PrivateSends;
        public bool Confirm = true;
        public List<string> SentTexts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.RequestUri!.AbsolutePath == "/get_login_info")
                return Response("{\"status\":\"ok\",\"retcode\":0,\"data\":{\"user_id\":99}}");
            Check(request.RequestUri.AbsolutePath == "/send_private_msg", "private responder selected a group API");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Check(body.RootElement.GetProperty("user_id").GetString() == "20" && !body.RootElement.TryGetProperty("group_id", out _),
                "private send selected the wrong contact or included group scope");
            PrivateSends++;
            SentTexts.Add(body.RootElement.GetProperty("message")[0].GetProperty("data").GetProperty("text").GetString()!);
            return Response(Confirm ? "{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":901}}" : "{\"status\":\"ok\",\"retcode\":0,\"data\":{}}");
        }
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static TaskCompletionSource Ready() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string MakeLibrary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qq-private-check-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(directory, "Files", "Memes"));
        File.WriteAllBytes(Path.Combine(directory, "Files", "Memes", "roger.png"), MemeLibraryChecks.Png);
        File.WriteAllText(Path.Combine(directory, "Files", "Memes", "manifest.json"),
            "{\"version\":1,\"items\":[{\"id\":\"roger\",\"file\":\"roger.png\",\"tags\":[\"收到\"]}]}");
        return directory;
    }
    private static void DeleteLibrary(string directory)
    {
        var resolved = Path.GetFullPath(directory);
        Check(resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "unexpected test cleanup target");
        Directory.Delete(resolved, true);
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
