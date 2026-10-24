using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

namespace AudioBridge;

public sealed record GroupChatActivity(DateTimeOffset At, long GroupId, long TriggerMessageId,
    string Trigger, string Status, bool ContainsBotName, string? ReplyMessageId);

public static class GroupChatRuntime
{
    private sealed record ChatWork(GroupChatDecision? Incoming = null, ScheduledMessageClaim? Scheduled = null);
    private sealed record OneBotConnection(string State, DateTimeOffset? CheckedAt = null, string? LastError = null);

    public static async Task RunAsync(GroupChatSettings settings, string groupId, double seconds, bool live,
        string engine, CancellationToken ct, TimeProvider? timeProvider = null, AdminRuntimeControl? control = null,
        BotProfileStore? profiles = null, ContactContextStore? contacts = null, PersonaSettings? persona = null,
        ReceivedImageInbox? receivedImages = null, ScheduledMessageStore? scheduledMessages = null,
        Action<ScheduledMessageRuntime?>? scheduleBinding = null, ScheduledMessageRateLimits? scheduledRateLimits = null)
    {
        if (!double.IsFinite(seconds) || seconds != 0 && seconds is < 10 or > 86400)
            throw new ArgumentException("chat seconds为0（持续运行）或10～86400秒。");
        if (!long.TryParse(groupId, NumberStyles.None, CultureInfo.InvariantCulture, out var group) || !settings.AllowedGroupIds.Contains(group))
            throw new ArgumentException("本次群号必须在group-chat.local.json白名单内。");
        if (engine is not ("fixed" or "deepseek" or "school")) throw new ArgumentException("engine为fixed、deepseek或school。");
        var project = Environment.CurrentDirectory;
        var token = BotSettings.LoadSecret(project, "ONEBOT_ACCESS_TOKEN") ?? throw new InvalidOperationException("请先配置本地ONEBOT_ACCESS_TOKEN。");
        var key = engine switch
        {
            "deepseek" => BotSettings.LoadApiKey(project) ?? throw new InvalidOperationException("请先配置本地DEEPSEEK_API_KEY，或用engine fixed预览。"),
            "school" => BotSettings.LoadSecret(project, "SCHOOL_API_KEY") ?? throw new InvalidOperationException("请先配置本地SCHOOL_API_KEY。"),
            _ => null
        };
        var local = Path.Combine(project, "Results", "local");
        Directory.CreateDirectory(local);
        using var exclusive = new FileStream(Path.Combine(local, "group-chat-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var client = new OneBotMessageClient(settings.OneBotBaseUrl, token,
            settings.AllowedGroupIds.Select(x => x.ToString(CultureInfo.InvariantCulture)),
            allowedPrivateUsers: settings.AllowedPrivateUserIds.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        var self = settings.SelfId;
        if (live || self == 0)
        {
            var logged = long.Parse(await client.GetLoginInfoAsync(ct), CultureInfo.InvariantCulture);
            if (self != 0 && self != logged) throw new InvalidOperationException("OneBot登录账号与配置selfId不同，本次不监听或发送。");
            self = logged;
        }
        var policy = new GroupChatPolicy(new() { AllowedGroupIds = [group], AllowedPrivateUserIds = settings.AllowedPrivateUserIds,
            ReservationTimeout = TimeSpan.FromSeconds(persona?.ReplyMaxCharacters > 448 ? 150 : 60),
            PrivateInterval = TimeSpan.FromSeconds(settings.PrivateIntervalSeconds), PrivatePerHour = settings.PrivatePerHour,
            MentionInterval = TimeSpan.FromSeconds(settings.MentionIntervalSeconds), MentionPerHour = settings.MentionPerHour,
            ActiveInterval = TimeSpan.FromSeconds(settings.ActiveIntervalSeconds), ActivePerHour = settings.ActivePerHour,
            ActiveMessageEvery = settings.ActiveMessageEvery,
            SelfId = self, BotNames = settings.BotNames, Mode = settings.ParsedMode() }, () => timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow);
        var memes = new MemeLibrary(project, settings.MemeDirectory);
        var school = engine == "school" ? SchoolModelSettings.Load(Path.Combine(project, "school-api.local.json")) : null;
        var model = engine == "school" ? school!.Model : engine == "deepseek" ? "deepseek-flash" : null;
        var apiBaseUrl = engine == "school" ? school!.BaseUrl : engine == "deepseek" ? "https://api.deepseek.com/" : null;
        var keyEnvironment = engine == "school" ? "SCHOOL_API_KEY" : engine == "deepseek" ? "DEEPSEEK_API_KEY" : null;
        var deepSeekBudget = engine == "deepseek" ? new ApiBudget(Path.Combine(local, "deepseek-budget.json"), .30, 10) : null;
        var schoolBudget = school is not null ? new SchoolUsageBudget(Path.Combine(local, "school-api-budget.json"), school) : null;
        object BudgetSnapshot() => schoolBudget?.Snapshot() ?? deepSeekBudget?.Snapshot() ?? new { scope = "fixed preview; no model requests" };
        // 群图和授权私聊联系人的图都进同一个入口；私聊按联系人分开取。
        var inbox = new GroupImageInbox(groupId, self.ToString(CultureInfo.InvariantCulture),
            allowedPrivateUsers: settings.AllowedPrivateUserIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        using var images = new ImageQuestionContext(inbox, groupId,
            rejectedHost: host => Write("image_url_rejected", new { host }));
        var vision = school is null || schoolBudget is null || string.IsNullOrWhiteSpace(school.VisionModel)
            ? null : new SchoolVision(school, key!, schoolBudget);
        var maximumOutputTokens = persona is null ? 256 : Math.Clamp(persona.ReplyMaxCharacters * 2 + 128, 256, 8192);
        var companions = new CompanionStore(project);
        // 心脑（日记整理与主动唤醒）使用当前文字引擎；预览引擎不调用模型。
        IStreamingReply ModelReply(string prompt) => engine switch
        {
            "deepseek" => new DeepSeekReply(key!, deepSeekBudget!, systemPrompt: prompt, jsonMode: true, maximumOutputTokens: 2048),
            "school" => new SchoolModelReply(school!, key!, schoolBudget!, prompt, maximumOutputTokens: 2048),
            _ => throw new InvalidOperationException("当前引擎不调用模型，心脑保持关闭。")
        };
        string ReferenceContext(ParsedGroupMessage? trigger, IReadOnlyList<ParsedGroupMessage> context)
        {
            try
            {
                var reference = BuildReferenceContext(trigger, context, profiles, contacts, persona);
                // 心情和日记只跟随明确触发的私聊联系人，不进入群聊请求。
                if (trigger is { IsPrivate: true }) reference += "\n" + companions.BuildContext(trigger.SenderId);
                return reference;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
            { Write("memory_unavailable", new { type = error.GetType().Name }); return ""; }
        }
        async Task<ImageContext> ImageContextAsync(ParsedGroupMessage? trigger, string question, CancellationToken token)
        {
            if (trigger is null) return ImageContext.Empty;
            try
            {
                var chatKey = trigger.IsPrivate
                    ? GroupImageInbox.PrivateKey(trigger.SenderId.ToString(CultureInfo.InvariantCulture))
                    : trigger.GroupId.ToString(CultureInfo.InvariantCulture);
                var result = await images.ResolveChatAsync(chatKey, trigger.MessageId, trigger.ImageSeen, question,
                    DateTimeOffset.UtcNow, token).ConfigureAwait(false);
                if (result.Clarification is { } clarification)
                {
                    Write("image_clarified", new { privateChat = trigger.IsPrivate, reason = clarification });
                    return new(clarification, [], "");
                }
                if (result.Images.Count == 0) return ImageContext.Empty;
                // 学校文字模型不能直读图片：先用视觉模型转述，再把文字交给它。
                if (engine != "school") return new(null, result.Images, "");
                if (vision is null) return new("这台机器上没有配置视觉模型。", [], "");
                var caption = await vision.DescribeAsync(question, result.Images, token).ConfigureAwait(false);
                Write("image_caption", new { privateChat = trigger.IsPrivate, images = result.Images.Count, characters = caption.Length });
                return new(null, [], caption);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException or InvalidOperationException)
            {
                // 读不到图时保持沉默，让文字模型按"没看到图"处理，而不是编造内容。
                Write("image_unavailable", new { type = error.GetType().Name,
                    message = error.Message.Length > 160 ? error.Message[..160] : error.Message });
                return new("这张图我这边读不出来。", [], "");
            }
        }
        void Archive(ParsedGroupMessage message, string text, string id, string role)
        {
            if (!message.IsPrivate || contacts is null) return;
            try { contacts.AppendMessage(message.SenderId, role, text, id); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
            { Write("memory_archive_failed", new { type = error.GetType().Name }); }
        }
        GroupChatBrain CreateBrain(bool isPrivate)
        {
            return new GroupChatBrain(memes.List, (question, currentMemes) =>
            {
                var prompt = GroupChatBrain.SystemPrompt(currentMemes.Select(m => (m.Id, m.Tags)), settings.ParsedMode(), isPrivate, persona);
                return engine switch
                {
                    // 图片由 GroupChatBrain 在生成前解析并附上，这里不再自行解析。
                    "deepseek" => new DeepSeekReply(key!, deepSeekBudget!, systemPrompt: prompt, jsonMode: true,
                        maximumOutputTokens: maximumOutputTokens),
                    "school" => new SchoolModelReply(school!, key!, schoolBudget!, prompt, maximumOutputTokens: maximumOutputTokens),
                    _ => new FixedReply(JsonSerializer.Serialize(new { send = true, text = "收到，这是文字和表情包预览。", memeId = currentMemes.Any(m => m.Id == "roger") ? "roger" : null }))
                };
            }, self, persona, ReferenceContext, ImageContextAsync);
        }
        await using var responder = new GroupChatResponder(policy, CreateBrain(false), memes, client, live, self);
        await using var privateResponder = new GroupChatResponder(policy, CreateBrain(true), memes, client, live, self,
            (message, text, id) => Archive(message, text, id, "assistant"));
        ScheduledMessageScope ScheduleScope()
        {
            try
            {
                // Saved revocations take effect before Apply. New targets still require a new configured client/session.
                var current = GroupChatSettings.Load(Path.Combine(project, "group-chat.local.json"));
                var selected = current.ActiveGroupId == 0 ? group : current.ActiveGroupId;
                var groups = current.AllowedGroupIds.Contains(group) && selected == group ? new[] { group } : [];
                var users = settings.AllowedPrivateUserIds.Intersect(current.AllowedPrivateUserIds).ToArray();
                if (current.OneBotBaseUrl != settings.OneBotBaseUrl || current.SelfId != 0 && current.SelfId != self)
                    return new(current.SelfId, [], [], true);
                return new(self, groups, users, policy.Snapshot().Paused);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            { return new(self, [], [], true); }
        }
        await using var schedules = scheduledMessages is null ? null : new ScheduledMessageRuntime(scheduledMessages, memes,
            client, self, live, ScheduleScope, (definition, spoken, receiptId) =>
            {
                if (definition.TargetType == "group") policy.RecordOwnReply(definition.TargetId, spoken);
                else
                {
                    policy.RecordOwnPrivateReply(definition.TargetId, spoken);
                    try { contacts?.AppendMessage(definition.TargetId, "assistant", spoken, receiptId); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
                    { Write("memory_archive_failed", new { type = error.GetType().Name }); }
                }
            }, () => policy.SetPaused(true), timeProvider, scheduledRateLimits);
        scheduleBinding?.Invoke(schedules);
        async Task SetPausedAsync(bool paused)
        {
            if (schedules is not null) await schedules.SetPausedAsync(paused);
            await responder.SetPausedAsync(paused);
            await privateResponder.SetPausedAsync(paused);
        }
        // InfiniteTimeSpan creates no deadline timer; external cancellation and Q still stop it.
        using var lifetime = new CancellationTokenSource(seconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds),
            timeProvider ?? TimeProvider.System);
        using var externalStop = ct.Register(lifetime.Cancel);
        // 主动消息和普通回复共用同一个 OneBot 客户端与每分钟限频记录；回执未知时不自动重发。
        async Task DeliverCompanionAsync(long userId, string text, CancellationToken token)
        {
            if (!live) return;
            var account = userId.ToString(CultureInfo.InvariantCulture);
            if (await client.GetLoginInfoAsync(token).ConfigureAwait(false) != self.ToString(CultureInfo.InvariantCulture))
            {
                policy.SetPaused(true);
                throw new InvalidOperationException("登录账号已变化，主动消息未发送。");
            }
            token.ThrowIfCancellationRequested();
            OneBotMessageReceipt receipt;
            try { receipt = await client.SendPrivateAsync(new(account, [OneBotSegment.Text(text)]), token).ConfigureAwait(false); }
            catch (OneBotMessageException error) when (error.DeliveryMayHaveOccurred)
            {
                // 无法确认是否已送达：记为已主动一次，避免重复发送；请人工核对聊天记录。
                Write("companion_wakeup_uncertain", new { userId, delivery = "unknown; check QQ before retry" });
                return;
            }
            policy.RecordOwnPrivateReply(userId, text);
            try { contacts?.AppendMessage(userId, "assistant", text, receipt.MessageId); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
            { Write("memory_archive_failed", new { type = error.GetType().Name }); }
        }
        var companionTask = contacts is null ? null : Task.Run(async () =>
        {
            try
            {
                var engineReply = new CompanionRuntime(companions, contacts, persona ?? new PersonaSettings(), ModelReply,
                    () => policy.Snapshot(), DeliverCompanionAsync, settings.AllowedPrivateUserIds, live, Write, timeProvider);
                await engineReply.RunAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
        var connection = new OneBotConnection(live ? "checking" : "preview");
        var health = Task.Run(async () =>
        {
            if (!live) return;
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    try
                    {
                        var status = await client.GetStatusAsync(lifetime.Token).ConfigureAwait(false);
                        var state = "offline";
                        if (status.Online && status.Good)
                        {
                            var currentAccount = await client.GetLoginInfoAsync(lifetime.Token).ConfigureAwait(false);
                            state = currentAccount == self.ToString(CultureInfo.InvariantCulture) ? "connected" : "account_changed";
                        }
                        if (state != "connected") schedules?.NotifyOffline();
                        Volatile.Write(ref connection, new(state, DateTimeOffset.UtcNow));
                    }
                    catch (OneBotMessageException error)
                    {
                        if (lifetime.IsCancellationRequested) break;
                        schedules?.NotifyOffline();
                        Volatile.Write(ref connection, new("unavailable", DateTimeOffset.UtcNow, error.GetType().Name));
                    }
                    await Task.Delay(TimeSpan.FromSeconds(30), lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
        // One reservation per contact plus the selected group; polling and event callbacks both write.
        var queue = Channel.CreateBounded<ChatWork>(new BoundedChannelOptions(Math.Max(8, 1 + settings.AllowedPrivateUserIds.Length))
            { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        var activityGate = new object();
        var recentActivity = new Queue<GroupChatActivity>();
        void Observe(GroupChatDecision decision, string status, string? replyMessageId = null)
        {
            if (decision.Message.IsPrivate) return;
            var entry = new GroupChatActivity(DateTimeOffset.UtcNow, decision.Message.GroupId, decision.Message.MessageId,
                decision.Trigger.ToString().ToLowerInvariant(), status,
                settings.BotNames.Any(name => decision.Message.Text.Contains(name, StringComparison.OrdinalIgnoreCase)), replyMessageId);
            lock (activityGate)
            {
                recentActivity.Enqueue(entry);
                while (recentActivity.Count > 20) recentActivity.Dequeue();
            }
        }
        GroupChatActivity[] ActivitySnapshot() { lock (activityGate) return recentActivity.ToArray(); }
        void Enqueue(GroupChatDecision decision)
        {
            Observe(decision, "queued");
            if (!queue.Writer.TryWrite(new(Incoming: decision)))
            { policy.Complete(decision.ReservationId!.Value, false); Observe(decision, "queue_full"); }
        }
        var server = new OneBotEventServer(settings.EventPort, token, json =>
        {
            if (!policy.TryParse(json, out var message, out _)) return false;
            if (!policy.Snapshot().Paused) receivedImages?.Observe(json, message!);
            inbox.AddEvent(json, DateTimeOffset.UtcNow);
            var decision = policy.Decide(message!);
            if (!decision.ShouldReply) Observe(decision, decision.Reason);
            if (message!.IsPrivate && decision.Reason != "duplicate")
                Archive(message, message.Text, message.MessageId.ToString(CultureInfo.InvariantCulture), "user");
            if (!decision.ShouldReply && decision.Reason is "cooldown" or "active_cooldown" or "active_pending" or "active_waiting" or "not_a_candidate" or "hourly_limit" or "busy" or "private_pending" or "private_pending_capacity")
                Write(decision.Reason == "private_pending" ? "message_pending" : "message_skipped", new { decision.Message.IsPrivate, decision.Message.GroupId,
                    userId = decision.Message.IsPrivate ? decision.Message.SenderId : 0,
                    decision.Message.MessageId, decision.Reason });
            if (decision.ShouldReply) Enqueue(decision);
            return true;
        });
        var listening = server.RunAsync(lifetime.Token);
        long lastReplyMicroseconds = -1;
        string? lastError = null;
        control?.Bind(() => new
        {
            running = server.IsRunning, paused = policy.Snapshot().Paused, oneBot = Volatile.Read(ref connection), engine, model, apiBaseUrl, keyEnvironment,
            groupId = group, privateUsers = settings.AllowedPrivateUserIds, policy = policy.Snapshot(), budget = BudgetSnapshot(),
            replyMaxCharacters = persona?.ReplyMaxCharacters ?? 160, maximumOutputTokens,
            lastReplyMilliseconds = Interlocked.Read(ref lastReplyMicroseconds) is var value && value >= 0 ? (double?)value / 1000 : null,
            lastError = Volatile.Read(ref lastError), recentGroupActivity = ActivitySnapshot(), scheduledMessages = schedules?.Snapshot()
        }, SetPausedAsync);
        var worker = Task.Run(async () =>
        {
            try
            {
                await foreach (var work in queue.Reader.ReadAllAsync(lifetime.Token))
                {
                    if (work.Scheduled is { } claim)
                    {
                        try
                        {
                            var outcome = await schedules!.HandleAsync(claim, lifetime.Token);
                            Write("scheduled_reply", new { claim.TaskId, claim.Revision, claim.Definition.TargetType,
                                claim.Definition.TargetId, outcome.Status, outcome.MessageId });
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
                        { Write("scheduled_reply_failed", new { claim.TaskId, type = error.GetType().Name, automaticRetry = false }); }
                        continue;
                    }
                    var decision = work.Incoming!;
                    try
                    {
                        var handler = decision.Message.IsPrivate ? privateResponder : responder;
                        var replyTimer = System.Diagnostics.Stopwatch.StartNew();
                        var outcome = await handler.HandleAsync(decision, lifetime.Token);
                        Interlocked.Exchange(ref lastReplyMicroseconds, (long)(replyTimer.Elapsed.TotalMilliseconds * 1000));
                        Volatile.Write(ref lastError, null);
                        Observe(decision, outcome.Status, outcome.MessageId);
                        Write("reply", new { decision.Message.IsPrivate, decision.Message.GroupId,
                            userId = decision.Message.IsPrivate ? decision.Message.SenderId : 0,
                            trigger = decision.Trigger.ToString(), triggerMessageId = decision.Message.MessageId,
                            outcome.Status, outcome.MemeId, outcome.MessageId });
                    }
                    catch (OperationCanceledException) { Observe(decision, "cancelled"); Write("reply_cancelled", new { pendingMessageNotRetried = true }); }
                    catch (Exception error)
                    {
                        Volatile.Write(ref lastError, error.GetType().Name);
                        Observe(decision, "failed");
                        // Transport wrappers expose only sanitized messages; never print raw request/response bodies.
                        Write("reply_failed", new { type = error.GetType().Name,
                            delivery = error is OneBotMessageException oneBot && oneBot.DeliveryMayHaveOccurred ? "unknown; check QQ before retry" : "not confirmed", automaticRetry = false });
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
        Write("started", new { group, privateUsers = settings.AllowedPrivateUserIds, selfId = self,
            mode = settings.Mode, live, engine, model, apiBaseUrl, keyEnvironment, server.Prefix,
            durationSeconds = seconds, continuous = seconds == 0,
            controls = "P暂停/恢复，X暂停并取消当前回复，S状态，Q退出；不采集或播放语音。" });
        try
        {
            var next = DateTimeOffset.UtcNow;
            var sessionStartedAt = timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
            var nextScheduledPoll = DateTimeOffset.MinValue;
            while (!lifetime.IsCancellationRequested)
            {
                if (listening.IsCompleted) { await listening; break; }
                if (worker.IsFaulted) await worker;
                foreach (var pending in policy.TakeReadyPrivateReplies())
                    Enqueue(pending);
                foreach (var pending in policy.TakeReadyActiveReplies())
                    Enqueue(pending);
                var now = timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
                if (schedules is not null && now >= nextScheduledPoll)
                {
                    try
                    {
                        foreach (var claim in await schedules.PollAsync(sessionStartedAt, lifetime.Token))
                            if (!queue.Writer.TryWrite(new(Scheduled: claim))) scheduledMessages!.Complete(claim, "skipped_busy");
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
                    { Write("schedule_poll_failed", new { type = error.GetType().Name }); }
                    nextScheduledPoll = now.AddSeconds(1);
                }
                if (!Console.IsInputRedirected && Console.KeyAvailable)
                {
                    switch (Console.ReadKey(true).Key)
                    {
                        case ConsoleKey.Q: lifetime.Cancel(); break;
                        case ConsoleKey.P: await SetPausedAsync(!policy.Snapshot().Paused); break;
                        case ConsoleKey.X: await SetPausedAsync(true); break;
                        case ConsoleKey.S: next = DateTimeOffset.MinValue; break;
                    }
                }
                if (DateTimeOffset.UtcNow >= next)
                { Write("status", new { server.IsRunning, engine, model, apiBaseUrl, keyEnvironment,
                    policy = policy.Snapshot(), budget = BudgetSnapshot() }); next = DateTimeOffset.UtcNow.AddSeconds(15); }
                await Task.Delay(25, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            var pausing = SetPausedAsync(true);
            queue.Writer.TryComplete();
            await Task.WhenAll(listening, worker, pausing, health, companionTask ?? Task.CompletedTask);
            Write("stopped", new { automaticSending = false, voiceStarted = false });
            scheduleBinding?.Invoke(null);
            control?.Clear();
        }
    }

    internal static string BuildReferenceContext(ParsedGroupMessage? trigger, IReadOnlyList<ParsedGroupMessage> context,
        BotProfileStore? profiles, ContactContextStore? contacts, PersonaSettings? persona)
    {
        // Only the explicit trigger owns a contact's notes. Never infer a group member from the last context item.
        if (trigger is null) return "";
        if (!trigger.IsPrivate)
        {
            var notes = persona?.ShareContactMemoriesInGroups == true ? profiles?.BuildGroupMemoryContext(trigger.SenderId) ?? "" : "";
            return notes + "\n" + (contacts?.BuildGroupPreferenceContext(trigger.SenderId) ?? "");
        }
        var privateNotes = profiles?.BuildPrivateMemoryContext(trigger.SenderId) ?? "";
        var excluded = context.Where(item => item.MessageId != 0).Select(item => item.MessageId.ToString(CultureInfo.InvariantCulture)).ToArray();
        return privateNotes + "\n" + (contacts?.BuildPrivateHistoryContext(trigger.SenderId, excluded) ?? "");
    }

    private static void Write(string status, object details) => Console.WriteLine(JsonSerializer.Serialize(
        new { command = "chat", status, local = DateTimeOffset.Now, details }, BridgeConfig.JsonOptions));
}
