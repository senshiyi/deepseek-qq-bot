using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

namespace AudioBridge;

public sealed record GroupChatActivity(DateTimeOffset At, long GroupId, long TriggerMessageId,
    string Trigger, string Status, bool ContainsBotName, string? ReplyMessageId);

public static class GroupChatRuntime
{
    private sealed record ChatWork(GroupChatDecision? Incoming = null, ScheduledMessageClaim? Scheduled = null);

    private sealed class Disposal(IEnumerable<IAsyncDisposable> items) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var item in items) await item.DisposeAsync().ConfigureAwait(false);
        }
    }
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
        var voice = LoadVoice(project, Write);
        var modelDirectory = BotSettings.Load(Path.Combine(project, "bot.local.json")).ModelDirectory;
        using var web = new PublicWeb(Write);
        using var voiceReader = new QqVoiceReader(client, modelDirectory, Write);
        var selfMemes = new SelfMemeLog(Path.Combine(local, "meme-self.json"));
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
        var deepSeekBudget = engine == "deepseek" ? new ApiBudget(Path.Combine(local, "deepseek-budget.json"), settings.ApiBudgetYuan, settings.MaxApiCalls) : null;
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
        // 语音引擎：按 voice.local.json 选 breeze 或 edge，失败自动回退；每条发出的语音同时在 Results/local/voice-sent 留档。
        var voiceArchive = Path.Combine(local, "voice-sent");
        using var speech = voice.Enabled
            ? new SpeechEngine(voice, companions, Write, voiceArchiveDirectory: voiceArchive) : null;
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
                reference += CompanionReference(trigger, companions);
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
                // 私聊原文里那条消息原来只有 [图片]；把转述补进去，面板和之后的上下文都能看出图里是什么。
                if (trigger.IsPrivate && contacts is not null)
                {
                    try { if (contacts.AnnotateLatestImage(trigger.SenderId, caption, trigger.MessageId)) Write("image_note_saved", new { characters = caption.Length }); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
                    { Write("memory_archive_failed", new { type = error.GetType().Name }); }
                }
                // 文字模型只读转述，但图片字节仍要留下：这一轮可以拿它自己收藏表情包。
                return new(null, result.Images, caption);
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
            if (contacts is null) return;
            try { contacts.AppendMessage(message.SenderId, role, text, id); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
            { Write("memory_archive_failed", new { type = error.GetType().Name }); }
        }
        // 私聊按联系人取人格：专属设置覆盖全局，没设置就是用全局。
        PersonaSettings PersonaFor(long userId)
        {
            if (persona is null) return new PersonaSettings();
            if (profiles is null) return persona;
            try { return profiles.EffectivePersona(userId); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
            { Write("persona_unavailable", new { userId, type = error.GetType().Name }); return persona; }
        }
        // 上网只读公开网页；失败原因回给模型，让它如实说没打开，而不是编造内容。
        async Task<string> ReadWebAsync(string url, CancellationToken token)
        {
            try
            {
                var page = await web.ReadPageAsync(url, token).ConfigureAwait(false);
                Write("web_read", new { host = new Uri(page.Url).Host, title = page.Title.Length, characters = page.Text.Length });
                return (page.Title.Length == 0 ? "" : "标题：" + page.Title + "\n") + page.Text;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException or HttpRequestException)
            {
                Write("web_failed", new { type = error.GetType().Name, code = ReplyFailure.From(error).Code });
                throw;
            }
        }
        // 自己收藏表情包：用对方刚发的图（或它给的网络直链），按内容哈希去重，每天最多四十张。
        async Task<string?> SaveMemeAsync(ParsedGroupMessage? trigger, SaveMemeRequest request, CancellationToken token)
        {
            if (trigger is null) { Write("meme_learn_skipped", new { reason = "no_trigger" }); return null; }
            byte[] bytes;
            try
            {
                if (request.Url is { } url) bytes = await web.DownloadImageAsync(url, token).ConfigureAwait(false);
                else
                {
                    var chatKey = trigger.IsPrivate
                        ? GroupImageInbox.PrivateKey(trigger.SenderId.ToString(CultureInfo.InvariantCulture))
                        : trigger.GroupId.ToString(CultureInfo.InvariantCulture);
                    // 收藏比"看图"宽松一点：用收图队列允许的上限（5 分钟），发完图再补一句"存一下"也来得及。
                    var image = await images.ResolveNewestAsync(chatKey, token, GroupImageInbox.MaximumEventAge).ConfigureAwait(false);
                    if (image is null)
                    {
                        Write("meme_learn_skipped", new { reason = "no_recent_image", trigger.IsPrivate });
                        return null;
                    }
                    bytes = Convert.FromBase64String(image.DataUrl[(image.DataUrl.IndexOf(',') + 1)..]);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            // 收藏失败不能连累这一轮回复，但也不能静默：把类型记下来，下回能直接定位。
            catch (Exception error)
            {
                Write("meme_learn_failed", new { stage = "source", type = error.GetType().Name, code = ReplyFailure.From(error).Code });
                return null;
            }
            var id = "self-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12].ToLowerInvariant();
            try
            {
                if (memes.List().Any(entry => entry.Id == id)) return id;
                if (!selfMemes.TryConsume(timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow))
                { Write("meme_learn_skipped", new { reason = "daily_limit" }); return null; }
                var entry = memes.ImportBytes(id, bytes, request.Tags);
                Write("meme_learned", new { id = entry.Id, tags = entry.Tags.Count, bytes = bytes.Length, fromUrl = request.Url is not null });
                return entry.Id;
            }
            catch (Exception error)
            {
                Write("meme_learn_failed", new { stage = "store", type = error.GetType().Name, code = ReplyFailure.From(error).Code });
                return null;
            }
        }
        // 自己联网找表情包：搜到的图由本地下载、校验、按内容哈希去重入库，结果如实回报给模型。
        async Task<MemeFindResult> FindMemeAsync(string keyword, CancellationToken token)
        {
            IReadOnlyList<WebImage> found;
            try { found = await web.SearchImagesAsync(keyword, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                Write("meme_search_failed", new { stage = "search", type = error.GetType().Name, code = ReplyFailure.From(error).Code });
                return new("这次没搜成（联网失败），没有收藏到任何图。");
            }
            Write("meme_search", new { keyword, candidates = found.Count });
            if (found.Count == 0) return new("没搜到合适的图，这次没有收藏。");
            var attempts = 0;
            foreach (var candidate in found)
            {
                if (attempts++ >= 3) break;
                byte[] bytes;
                try { bytes = await web.DownloadImageAsync(candidate.Url, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { continue; }
                var id = "self-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12].ToLowerInvariant();
                try
                {
                    if (memes.List().Any(entry => entry.Id == id))
                        return new("搜到的这张库里已经有了，不用重复收藏，也不用再给标签。");
                    if (!selfMemes.TryConsume(timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow))
                        return new("今天的收藏额度（" + selfMemes.DailyLimit + "张）用完了，这次没存。");
                    var entry = memes.ImportBytes(id, bytes, [keyword]);
                    Write("meme_found", new { id = entry.Id, bytes = bytes.Length, keyword });
                    // 转述一次，让模型按"图里是什么"自己定标签，而不是照抄搜索关键词。
                    var caption = await CaptionAsync(bytes, token).ConfigureAwait(false);
                    return new("已经联网找到并收藏了一张与「" + keyword + "」有关的表情包，下一轮可以自己发它。", entry.Id, caption);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    Write("meme_search_failed", new { stage = "store", type = error.GetType().Name, keyword });
                    return new("搜到了图但没存进来，这次没有收藏成功。");
                }
            }
            return new("搜到的几张都用不了，这次没有收藏。");
        }
        // 给刚存下的新图做一次转述，失败就返回 null：模型仍可凭关键词起标签。
        async Task<string?> CaptionAsync(byte[] bytes, CancellationToken token)
        {
            if (vision is null) return null;
            try
            {
                var mime = MemeImageFormat.Validate(bytes);
                var caption = await vision.DescribeAsync("", [new InlineImage(mime, "data:" + mime + ";base64," + Convert.ToBase64String(bytes))], token)
                    .ConfigureAwait(false);
                Write("meme_caption", new { characters = caption.Length });
                return caption;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                Write("meme_caption_failed", new { type = error.GetType().Name, code = ReplyFailure.From(error).Code });
                return null;
            }
        }
        // 标签由模型自己判断，本地只做一次覆盖写。
        async Task TagMemeAsync(string id, string[] tags, CancellationToken token)
        {
            try
            {
                var entry = memes.UpdateTags(id, tags);
                Write("meme_tagged", new { id = entry.Id, tags = entry.Tags.Count });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                Write("meme_tag_failed", new { id, type = error.GetType().Name });
            }
            await Task.CompletedTask;
        }
        Func<string, CancellationToken, Task<byte[]>> fetchImage = (url, token) => web.DownloadImageAsync(url, token);
        Func<VoiceCue, CancellationToken, Task<byte[]>>? synthesizeVoice = speech is null ? null : speech.SynthesizeAsync;
        // 群聊里如果触发者是有专属人格的白名单联系人，就用他的人格；管理员关掉"群聊沿用角色"时仍用全局。
        PersonaSettings? GroupPersonaFor(long userId)
        {
            if (persona is null || profiles is null) return persona;
            try
            {
                var effective = profiles.EffectivePersona(userId);
                return effective.UsePersonaInGroups ? effective : persona;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
            { Write("persona_unavailable", new { userId, type = error.GetType().Name }); return persona; }
        }
        GroupChatBrain CreateBrain(bool isPrivate, PersonaSettings? effective = null)
        {
            var chosen = effective ?? persona;
            return new GroupChatBrain(memes.List, (question, currentMemes, speakerPersona) =>
            {
                var prompt = GroupChatBrain.SystemPrompt(currentMemes.Select(m => (m.Id, m.Tags)), settings.ParsedMode(), isPrivate, speakerPersona,
                    voice.Enabled, speech?.EmotionEnabled == true);
                return engine switch
                {
                    // 图片由 GroupChatBrain 在生成前解析并附上，这里不再自行解析。
                    "deepseek" => new DeepSeekReply(key!, deepSeekBudget!, systemPrompt: prompt, jsonMode: true,
                        maximumOutputTokens: maximumOutputTokens),
                    "school" => new SchoolModelReply(school!, key!, schoolBudget!, prompt, maximumOutputTokens: maximumOutputTokens),
                    _ => new FixedReply(JsonSerializer.Serialize(new { send = true, text = "收到，这是文字和表情包预览。", memeId = currentMemes.Any(m => m.Id == "roger") ? "roger" : null }))
                };
            }, self, chosen, ReferenceContext, ImageContextAsync, ReadWebAsync, SaveMemeAsync, FindMemeAsync, TagMemeAsync,
                personaFor: isPrivate ? null : GroupPersonaFor, voiceEnabled: voice.Enabled);
        }
        await using var responder = new GroupChatResponder(policy, CreateBrain(false), memes, client, live, self,
            fetchImage: fetchImage, synthesizeVoice: synthesizeVoice);
        // 每位白名单联系人有自己的 brain（人格、称呼、表述都按他取），按发送者分派。
        var privateResponders = settings.AllowedPrivateUserIds.Distinct().ToDictionary(user => user,
            user => new GroupChatResponder(policy, CreateBrain(true, PersonaFor(user)), memes, client, live, self,
                (message, text, id) => Archive(message, text, id, "assistant"), null, fetchImage, synthesizeVoice));
        await using var privateDisposer = new Disposal(privateResponders.Values);
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
            foreach (var item in privateResponders.Values) await item.SetPausedAsync(paused);
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
        var companionHealth = contacts is null ? null : new BackgroundTaskMonitor("companion", Write, timeProvider);
        // 自己整理长期笔记：每半小时看一眼新私聊，只看没处理过的消息，每天最多六条。
        var memoryHealth = contacts is null || profiles is null ? null : new BackgroundTaskMonitor("memory-learner", Write, timeProvider);
        var memoryLearner = memoryHealth is null ? null : new MemoryLearner(profiles!, contacts!,
            new MemoryLearnStore(Path.Combine(local, "memory-learn.json")), ModelReply, live, Write, timeProvider);
        var connectionHealth = live ? new BackgroundTaskMonitor("onebot-health", Write, timeProvider) : null;
        var listenerHealth = new BackgroundTaskMonitor("message-listener", Write, timeProvider);
        var workerHealth = new BackgroundTaskMonitor("message-worker", Write, timeProvider);
        // 收到的 QQ 语音要先转文字才进决定流程；识别失败也要能在面板上看到。
        var voiceHealth = new BackgroundTaskMonitor("voice-input", Write, timeProvider);
        voiceHealth.MarkRunning();
        BackgroundTaskSnapshot[] BackgroundSnapshot() => new[] { listenerHealth, workerHealth, connectionHealth, companionHealth, memoryHealth, voiceHealth }
            .OfType<BackgroundTaskMonitor>().Select(task => task.Snapshot()).ToArray();
        // 空间发布只在真实运行时接上；取登录凭证和发说说都走同一个 OneBot 客户端。
        Func<string, CancellationToken, Task>? publishQzone = live
            ? new QzonePublisher(token => client.GetQzoneCookiesAsync(token), (status, details) => Write(status, details)).PublishAsync
            : null;
        var qzoneState = new QzoneStore(project);
        // 空间评论走页面读取（空间 CGI 对非浏览器会话不可用），读到后用私聊回答。
        var qzoneReader = live ? new QzoneFeedReader(token => client.GetQzoneCookiesAsync(token)) : null;
        Func<CancellationToken, Task<IReadOnlyList<QzoneComment>>>? qzoneComments = qzoneReader is null ? null : async token =>
            (await qzoneReader.ReadAsync(token).ConfigureAwait(false)).SelectMany(feed => feed.Comments).ToArray();
        var companionTask = contacts is null ? null : Task.Run(() => companionHealth!.RunAsync(async token =>
        {
            var engineReply = new CompanionRuntime(companions, contacts, persona ?? new PersonaSettings(), ModelReply,
                () => policy.Snapshot(), DeliverCompanionAsync, settings.AllowedPrivateUserIds, live, Write, timeProvider,
                companionHealth.Report, publishQzone, qzoneState, qzoneComments, PersonaFor);
            await engineReply.RunAsync(token).ConfigureAwait(false);
        }, lifetime.Token));
        var memoryTask = memoryLearner is null ? null : Task.Run(() => memoryHealth!.RunAsync(async token =>
        {
            // 启动后两分钟先整理一次已有的私聊，之后每半小时看一眼新消息。
            var first = true;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(first ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(30), token).ConfigureAwait(false);
                first = false;
                if (policy.Snapshot().Paused || !live || engine == "fixed") continue;
                try
                {
                    var result = await memoryLearner.RunOnceAsync(settings.AllowedPrivateUserIds, token).ConfigureAwait(false);
                    memoryHealth.Report(result.Error);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                    or InvalidDataException or TimeoutException or HttpRequestException or InvalidOperationException)
                {
                    memoryHealth.Report(error);
                    Write("memory_learn_failed", new { type = error.GetType().Name, code = ReplyFailure.From(error).Code });
                }
            }
        }, lifetime.Token));
        var connection = new OneBotConnection(live ? "checking" : "preview");
        var health = connectionHealth is null ? Task.CompletedTask : Task.Run(() => connectionHealth.RunAsync(async token =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var status = await client.GetStatusAsync(token).ConfigureAwait(false);
                    var state = "offline";
                    if (status.Online && status.Good)
                    {
                        var currentAccount = await client.GetLoginInfoAsync(token).ConfigureAwait(false);
                        state = currentAccount == self.ToString(CultureInfo.InvariantCulture) ? "connected" : "account_changed";
                    }
                    if (state != "connected") schedules?.NotifyOffline();
                    Volatile.Write(ref connection, new(state, DateTimeOffset.UtcNow));
                    connectionHealth.Report(state == "connected" ? null : new InvalidOperationException("账号未就绪。"));
                }
                catch (OneBotMessageException error)
                {
                    if (token.IsCancellationRequested) break;
                    schedules?.NotifyOffline();
                    Volatile.Write(ref connection, new("unavailable", DateTimeOffset.UtcNow, error.GetType().Name));
                    connectionHealth.Report(error);
                }
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            }
        }, lifetime.Token));
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
        // 好友申请不是消息：自动同意有独立限速，不进入聊天队列，也不改动私聊白名单。
        var friendRequests = new FriendRequestAcceptor(client.ApproveFriendRequestAsync, () => policy.Snapshot().Paused,
            self, Write, live, () => timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow, lifetime.Token);
        // 语音消息先转成本地文字，再走同一套决定流程：这样频率、去重、记忆都用真实内容。
        async Task<string> TranscribeAsync(string recordFile, CancellationToken token)
        {
            try
            {
                var text = await voiceReader.TranscribeAsync(recordFile, token).ConfigureAwait(false);
                voiceHealth.Report(null);
                Write("voice_heard", new { characters = text.Length });
                return text.Length == 0 ? "（语音没听清）" : "语音：" + text;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException or OneBotMessageException or NotSupportedException or FileNotFoundException)
            {
                voiceHealth.Report(error);
                Write("voice_unavailable", new { type = error.GetType().Name, code = ReplyFailure.From(error).Code });
                return "语音：我收到了一条语音，但这边没听清。";
            }
        }
        // 图一到就先转述并存进原文，不管她这一轮回不回复：否则没被触发的图永久只剩[图片]。
        void CaptionArchivedImage(ParsedGroupMessage message)
        {
            if (vision is null || contacts is null || !settings.AllowedPrivateUserIds.Contains(message.SenderId)) return;
            var store = contacts;
            var chatKey = message.IsPrivate
                ? GroupImageInbox.PrivateKey(message.SenderId.ToString(CultureInfo.InvariantCulture))
                : message.GroupId.ToString(CultureInfo.InvariantCulture);
            _ = Task.Run(async () =>
            {
                try
                {
                    var frames = await images.ResolveMessageAsync(chatKey, message.MessageId, lifetime.Token).ConfigureAwait(false);
                    if (frames.Count == 0) return;
                    // 面板要能直接看原图：先把字节存下来，转述失败也不影响看得到图。
                    if (frames[0].TryDecode() is { } bytes)
                    {
                        try { store.SaveImage(message.SenderId, message.MessageId.ToString(CultureInfo.InvariantCulture), frames[0].MimeType, bytes); }
                        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
                        { Write("image_store_failed", new { type = error.GetType().Name }); }
                    }
                    var caption = await vision.DescribeAsync("这张图里是什么？", frames, lifetime.Token).ConfigureAwait(false);
                    if (store.AnnotateLatestImage(message.SenderId, caption, message.MessageId))
                        Write("image_note_saved", new { privateChat = message.IsPrivate, characters = caption.Length });
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException or InvalidOperationException or HttpRequestException)
                { Write("image_unavailable", new { privateChat = message.IsPrivate, type = error.GetType().Name }); }
            }, lifetime.Token);
        }
        void Process(ParsedGroupMessage message)
        {
            try
            {
                var decision = policy.Decide(message);
                if (!decision.ShouldReply) Observe(decision, decision.Reason);
                if (message.IsPrivate && decision.Reason != "duplicate")
                    Archive(message, message.Text, message.MessageId.ToString(CultureInfo.InvariantCulture), "user");
                // 白名单联系人在群里说的也算他的记忆；加[群聊]前缀，和私聊原文区分开。
                else if (!message.IsPrivate && decision.Reason != "duplicate" &&
                    settings.AllowedPrivateUserIds.Contains(message.SenderId))
                    Archive(message, "[群聊] " + message.Text, message.MessageId.ToString(CultureInfo.InvariantCulture), "user");
                if (message.ImageSeen && decision.Reason != "duplicate") CaptionArchivedImage(message);
                if (!decision.ShouldReply && decision.Reason is "cooldown" or "active_cooldown" or "active_pending" or "active_waiting" or "not_a_candidate" or "hourly_limit" or "busy" or "private_pending" or "private_pending_capacity")
                    Write(decision.Reason == "private_pending" ? "message_pending" : "message_skipped", new { decision.Message.IsPrivate, decision.Message.GroupId,
                        userId = decision.Message.IsPrivate ? decision.Message.SenderId : 0,
                        decision.Message.MessageId, decision.Reason });
                if (decision.ShouldReply) Enqueue(decision);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
            { Write("message_processing_failed", new { type = error.GetType().Name }); }
        }
        var server = new OneBotEventServer(settings.EventPort, token, json =>
        {
            if (friendRequests.TryHandle(json)) return true;
            if (!policy.TryParse(json, out var message, out _)) return false;
            if (!policy.Snapshot().Paused) receivedImages?.Observe(json, message!);
            inbox.AddEvent(json, DateTimeOffset.UtcNow);
            listenerHealth.Report(null);
            if (message!.VoiceFile is { } recordFile)
            {
                // 语音识别要 1～2 秒，不能占着事件回执：先缓存消息，识别完再决定。
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var text = await TranscribeAsync(recordFile, lifetime.Token).ConfigureAwait(false);
                        Process(message with { Text = text });
                    }
                    catch (OperationCanceledException) { }
                }, lifetime.Token);
                return true;
            }
            Process(message);
            return true;
        });
        var listening = listenerHealth.RunAsync(server.RunAsync, lifetime.Token, propagateFailure: true);
        long lastReplyMicroseconds = -1;
        control?.Bind(() =>
        {
            var processing = workerHealth.Snapshot();
            return new
            {
                running = server.IsRunning, paused = policy.Snapshot().Paused, oneBot = Volatile.Read(ref connection), engine, model, apiBaseUrl, keyEnvironment,
                groupId = group, privateUsers = settings.AllowedPrivateUserIds, policy = policy.Snapshot(), budget = BudgetSnapshot(),
                replyMaxCharacters = persona?.ReplyMaxCharacters ?? 160, maximumOutputTokens,
                voiceEnabled = voice.Enabled, voiceName = voice.Voice, ttsProvider = voice.Enabled ? voice.TtsProvider : "off",
                voiceEmotion = speech?.EmotionEnabled == true,
                lastReplyMilliseconds = Interlocked.Read(ref lastReplyMicroseconds) is var value && value >= 0 ? (double?)value / 1000 : null,
                lastError = processing.LastError, lastErrorCode = processing.LastErrorCode, lastErrorMessage = processing.LastErrorMessage,
                lastErrorAt = processing.LastResult == "failed" ? processing.LastResultAt : null,
                recentGroupActivity = ActivitySnapshot(), scheduledMessages = schedules?.Snapshot(),
                backgroundTasks = BackgroundSnapshot()
            };
        }, SetPausedAsync);
        var worker = Task.Run(() => workerHealth.RunAsync(async _ =>
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
                            var failure = outcome.Status is "failed" or "delivery_unknown"
                                ? ReplyFailure.Tag(new IOException(), outcome.Status == "failed" ? "scheduled_failed" : "delivery_unknown") : null;
                            workerHealth.Report(failure);
                            var diagnostic = failure is null ? null : ReplyFailure.From(failure);
                            Write("scheduled_reply", new { claim.TaskId, claim.Revision, claim.Definition.TargetType,
                                claim.Definition.TargetId, outcome.Status, outcome.MessageId, code = diagnostic?.Code, reason = diagnostic?.Message });
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
                        {
                            workerHealth.Report(error);
                            var failure = ReplyFailure.From(error);
                            Write("scheduled_reply_failed", new { claim.TaskId, type = error.GetType().Name,
                                code = failure.Code, reason = failure.Message, automaticRetry = false });
                        }
                        continue;
                    }
                    var decision = work.Incoming!;
                    try
                    {
                        var handler = decision.Message.IsPrivate &&
                            privateResponders.TryGetValue(decision.Message.SenderId, out var privateHandler) ? privateHandler : responder;
                        var replyTimer = System.Diagnostics.Stopwatch.StartNew();
                        var outcome = await handler.HandleAsync(decision, lifetime.Token);
                        Interlocked.Exchange(ref lastReplyMicroseconds, (long)(replyTimer.Elapsed.TotalMilliseconds * 1000));
                        Observe(decision, outcome.Status, outcome.MessageId);
                        Write("reply", new { decision.Message.IsPrivate, decision.Message.GroupId,
                            userId = decision.Message.IsPrivate ? decision.Message.SenderId : 0,
                            trigger = decision.Trigger.ToString(), triggerMessageId = decision.Message.MessageId,
                            outcome.Status, outcome.MemeId, outcome.MessageId, outcome.Voice });
                        workerHealth.Report(null);
                    }
                    catch (OperationCanceledException) { Observe(decision, "cancelled"); Write("reply_cancelled", new { pendingMessageNotRetried = true }); }
                    catch (Exception error)
                    {
                        workerHealth.Report(error);
                        Observe(decision, "failed");
                        // Transport wrappers expose only sanitized messages; never print raw request/response bodies.
                        var failure = ReplyFailure.From(error);
                        Write("reply_failed", new { type = error.GetType().Name,
                            code = failure.Code, reason = failure.Message,
                            unknownField = error.Data[GroupChatBrain.UnknownFieldKey] as string,
                            delivery = error is OneBotMessageException oneBot && oneBot.DeliveryMayHaveOccurred ? "unknown; check QQ before retry" : "not confirmed", automaticRetry = false });
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }, lifetime.Token, propagateFailure: true));
        Write("started", new { group, privateUsers = settings.AllowedPrivateUserIds, selfId = self,
            mode = settings.Mode, live, engine, model, apiBaseUrl, keyEnvironment, server.Prefix,
            voice = voice.Enabled, voiceName = voice.Voice, ttsProvider = voice.Enabled ? voice.TtsProvider : "off",
            durationSeconds = seconds, continuous = seconds == 0,
            controls = "P暂停/恢复，X暂停并取消当前回复，S状态，Q退出；不采集电脑麦克风，QQ 语音消息单独按 voice.local.json 发送。" });
        try
        {
            var next = DateTimeOffset.UtcNow;
            var sessionStartedAt = timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
            var nextScheduledPoll = DateTimeOffset.MinValue;
            var nextVoiceCleanup = DateTimeOffset.MinValue;
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
                // 语音留档只留七天；启动时先清一次，之后每六小时看一眼。
                if (now >= nextVoiceCleanup)
                {
                    try
                    {
                        var removed = PurgeVoiceArchive(voiceArchive, now);
                        if (removed > 0) Write("voice_archive_cleaned", new { removed });
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    { Write("voice_archive_clean_failed", new { type = error.GetType().Name }); }
                    nextVoiceCleanup = now.AddHours(6);
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
                    policy = policy.Snapshot(), budget = BudgetSnapshot(), backgroundTasks = BackgroundSnapshot() }); next = DateTimeOffset.UtcNow.AddSeconds(15); }
                await Task.Delay(25, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            var pausing = SetPausedAsync(true);
            queue.Writer.TryComplete();
            try { await Task.WhenAll(listening, worker, pausing, health, companionTask ?? Task.CompletedTask, memoryTask ?? Task.CompletedTask); }
            finally
            {
                Write("stopped", new { automaticSending = false, voiceStarted = false });
                scheduleBinding?.Invoke(null);
                control?.Clear();
            }
        }
    }

    /// <summary>语音留档只留七天：到期删掉，想长期留就自己另存。ponytail: 固定七天，有人要别的期限再加配置。</summary>
    internal static int PurgeVoiceArchive(string directory, DateTimeOffset nowUtc)
    {
        if (!Directory.Exists(directory)) return 0;
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.wav", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (nowUtc - File.GetLastWriteTimeUtc(path) <= VoiceArchiveRetention) continue;
                File.Delete(path);
                removed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    private static readonly TimeSpan VoiceArchiveRetention = TimeSpan.FromDays(7);

    /// <summary>好感/占有欲/心情跟着触发的人走，群聊也算；私人日记只进私聊，免得她当着别人的面说出来。</summary>
    internal static string CompanionReference(ParsedGroupMessage? trigger, CompanionStore companions) =>
        trigger is null ? "" : "\n" + companions.BuildContext(trigger.SenderId, includeDiary: trigger.IsPrivate);

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

    private static VoiceSettings LoadVoice(string project, Action<string, object> log)
    {
        try { return VoiceSettings.Load(Path.Combine(project, "voice.local.json")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            // 语音配置坏了不该让聊天起不来：关掉语音，其余照常。
            log("voice_config_invalid", new { type = error.GetType().Name });
            return new VoiceSettings { Enabled = false };
        }
    }

    private static void Write(string status, object details) => Console.WriteLine(JsonSerializer.Serialize(
        new { command = "chat", status, local = DateTimeOffset.Now, details }, BridgeConfig.JsonOptions));
}
