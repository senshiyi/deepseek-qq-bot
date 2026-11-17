using System.Text.Json;

namespace AudioBridge;

/// <summary>
/// 心脑循环：按设置每天整理一次日记与心情，其余时间定时唤醒她，是否开口由模型决定。
/// 只处理私聊白名单联系人；预览模式不调用模型、不发送。
/// </summary>
public sealed class CompanionRuntime
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);
    private const int WakeContextCharacters = 1200;
    /// <summary>空间评论轮询间隔：空间页面能读，但别太密，十分钟一次足够。</summary>
    private static readonly TimeSpan CommentPoll = TimeSpan.FromMinutes(10);

    private readonly CompanionStore store;
    private readonly ContactContextStore contacts;
    private readonly PersonaSettings persona;
    private readonly Func<string, IStreamingReply> replyFactory;
    private readonly Func<GroupChatStatus> status;
    private readonly Func<long, string, CancellationToken, Task> deliver;
    private readonly IReadOnlyList<long> allowedUsers;
    private readonly bool live;
    private readonly TimeProvider timeProvider;
    private readonly Action<string, object> log;
    private readonly Action<Exception?>? tickCompleted;
    private readonly Func<string, CancellationToken, Task>? publishQzone;
    private readonly QzoneStore? qzone;
    private readonly Func<CancellationToken, Task<IReadOnlyList<QzoneComment>>>? qzoneComments;
    private readonly Func<long, PersonaSettings>? personaFor;
    private DateTimeOffset qzoneRetryAfter = DateTimeOffset.MinValue;
    private DateTimeOffset nextCommentPoll = DateTimeOffset.MinValue;
    private readonly Dictionary<long, DateTimeOffset> retryAfter = [];
    private DateTimeOffset nextPurge = DateTimeOffset.MinValue;

    public CompanionRuntime(CompanionStore store, ContactContextStore contacts, PersonaSettings persona,
        Func<string, IStreamingReply> replyFactory, Func<GroupChatStatus> status,
        Func<long, string, CancellationToken, Task> deliver, IReadOnlyList<long> allowedUsers, bool live,
        Action<string, object> log, TimeProvider? timeProvider = null, Action<Exception?>? tickCompleted = null,
        Func<string, CancellationToken, Task>? publishQzone = null, QzoneStore? qzone = null,
        Func<CancellationToken, Task<IReadOnlyList<QzoneComment>>>? qzoneComments = null,
        Func<long, PersonaSettings>? personaFor = null)
    {
        this.personaFor = personaFor;
        this.publishQzone = publishQzone;
        this.qzone = qzone;
        this.qzoneComments = qzoneComments;
        this.store = store; this.contacts = contacts; this.persona = persona; this.replyFactory = replyFactory;
        this.status = status; this.deliver = deliver; this.allowedUsers = allowedUsers; this.live = live;
        this.log = log; this.timeProvider = timeProvider ?? TimeProvider.System;
        this.tickCompleted = tickCompleted;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Tick, ct).ConfigureAwait(false);
                await TickAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        Exception? failure = null;
        CompanionSettings settings;
        try { settings = store.LoadSettings(); }
        catch (Exception error) when (Recoverable(error, ct))
        {
            log("companion_settings_unavailable", new { type = error.GetType().Name });
            tickCompleted?.Invoke(error);
            return;
        }
        if (!live) return;
        var state = status();
        if (state.Paused) return;
        var retryDeferred = retryAfter.Values.Any(until => now < until);
        if (now >= nextPurge)
        {
            try
            {
                var removed = store.PurgeExpired();
                if (removed > 0) log("companion_diary_forgotten", new { removed });
            }
            catch (Exception error) when (Recoverable(error, ct))
            { failure = error; log("companion_forget_failed", new { type = error.GetType().Name }); }
            nextPurge = now + PurgeInterval;
        }
        try { await PublishQzoneAsync(settings, now, ct).ConfigureAwait(false); }
        catch (Exception error) when (Recoverable(error, ct))
        {
            // 空间失败不重试：一小时内不再尝试，避免每 30 秒重复调用模型或打日志。
            qzoneRetryAfter = now.AddHours(1);
            failure = error;
            log("qzone_publish_failed", new { type = error.GetType().Name, reason = error.Message });
        }
        try { await ReplyToCommentsAsync(settings, now, ct).ConfigureAwait(false); }
        catch (Exception error) when (Recoverable(error, ct))
        {
            nextCommentPoll = now + CommentPoll;
            failure = error;
            log("qzone_comments_failed", new { type = error.GetType().Name, reason = error.Message });
        }
        foreach (var user in allowedUsers.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var contact = state.PrivateContacts.FirstOrDefault(item => item.UserId == user);
            try { await AuditAsync(user, settings, now, ct).ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error, ct))
            { failure = error; retryAfter[user] = now + RetryDelay; log("companion_diary_failed", new { userId = user, type = error.GetType().Name }); }
            try { await WakeAsync(user, settings, contact, now, ct).ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error, ct))
            { failure = error; retryAfter[user] = now + RetryDelay; log("companion_wakeup_failed", new { userId = user, type = error.GetType().Name }); }
        }
        ct.ThrowIfCancellationRequested();
        // 退避期间的空转不证明失败操作已恢复，保留面板上的失败状态。
        if (failure is not null || !retryDeferred) tickCompleted?.Invoke(failure);
    }

    /// <summary>该联系人实际使用的人格；读取失败时退回全局人格，不中断这一轮。</summary>
    private PersonaSettings PersonaFor(long userId)
    {
        if (personaFor is null) return persona;
        try { return personaFor(userId); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
        { log("persona_unavailable", new { userId, type = error.GetType().Name }); return persona; }
    }

    private static bool Recoverable(Exception error, CancellationToken ct) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            TimeoutException or OneBotMessageException or JsonException or HttpRequestException ||
        error is OperationCanceledException && !ct.IsCancellationRequested;

    /// <summary>每天一次：把这天的新私聊整理成日记和心情；没有可记的内容也记为已整理，避免反复调用。</summary>
    private async Task AuditAsync(long user, CompanionSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        if (!settings.Audit) return;
        if (retryAfter.TryGetValue(user, out var blocked) && now < blocked) return;
        var snapshot = store.Get(user);
        var boundary = CompanionStore.AuditBoundary(now, settings.AuditHour);
        if (!snapshot.AuditPending && snapshot.LastAuditAt is { } lastAudit && lastAudit >= boundary) return;
        var history = Unaudited(contacts.Recent(user, 1000), snapshot, now);
        if (history.Length == 0) { store.MarkAudited(user, now); return; }
        var batch = CompanionJournal.BuildBatch(history);
        var draft = await CompanionJournal.GenerateAsync(PersonaFor(user), snapshot.Mood, batch, replyFactory, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var completed = batch.Count == history.Length;
        var last = batch[^1];
        store.ApplyAuditBatch(user, draft, new CompanionAuditCursor(last.Id, last.At), completed ? now : null);
        log("companion_diary", new { userId = user, entries = draft.Entries.Count, moodUpdated = draft.Mood.Length > 0,
            processedMessages = batch.Count, pending = !completed, until = boundary.AddDays(1) });
    }

    private static StoredPrivateMessage[] Unaudited(IReadOnlyList<StoredPrivateMessage> history,
        CompanionSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.AuditCursor is not { } cursor)
            return history.Where(message => message.At > (snapshot.LastAuditAt ?? now.AddDays(-1))).ToArray();
        // 同一时间戳的消息沿用原始到达顺序，不能仅按时间跳过整个同秒区间。
        var position = -1;
        for (var index = 0; index < history.Count; index++)
            if (history[index].Id == cursor.MessageId) { position = index; break; }
        return position >= 0 ? history.Skip(position + 1).ToArray()
            // 游标已被七天/1000条保留策略清出时，同一时间的剩余消息也必须继续整理。
            : history.Where(message => message.At >= cursor.At).ToArray();
    }

    /// <summary>
    /// 读自己空间页面上的新评论，用私聊回应：空间回复接口对非浏览器会话不可用，
    /// 所以这里只在私聊里回；每条评论只处理一次，第一次运行把历史评论全部登记不回复。
    /// </summary>
    private async Task ReplyToCommentsAsync(CompanionSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        if (qzoneComments is null || qzone is null || !settings.Qzone || now < nextCommentPoll) return;
        nextCommentPoll = now + CommentPoll;
        var comments = await qzoneComments(ct).ConfigureAwait(false);
        var state = qzone.Load();
        if (!state.Seeded)
        {
            if (comments.Count > 0) qzone.MarkHandled(comments.Select(comment => comment.Id));
            qzone.MarkSeeded();
            log("qzone_comments_seeded", new { count = comments.Count });
            return;
        }
        var handled = state.HandledComments.ToHashSet(StringComparer.Ordinal);
        var fresh = comments.Where(comment => allowedUsers.Contains(comment.AuthorUin) && !handled.Contains(comment.Id)).Take(3).ToArray();
        if (fresh.Length == 0) return;
        var answered = new List<string>(fresh.Length);
        foreach (var comment in fresh)
        {
            ct.ThrowIfCancellationRequested();
            var reply = await GenerateAsync(BuildCommentPrompt(comment), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var text = reply.Send ? reply.Text.Trim() : "";
            if (text.Length == 0)
            {
                answered.Add(comment.Id);
                log("qzone_comment_skipped", new { commentId = comment.Id });
                continue;
            }
            await deliver(comment.AuthorUin, text, ct).ConfigureAwait(false);
            answered.Add(comment.Id);
            log("qzone_comment_replied", new { commentId = comment.Id, characters = text.Length });
        }
        if (answered.Count > 0) qzone.MarkHandled(answered);
    }

    private string BuildCommentPrompt(QzoneComment comment)
    {
        var current = PersonaFor(comment.AuthorUin);
        return "你叫「" + current.Name + "」，性格是：" + current.Personality + "。表达方式是：" + current.SpeakingStyle + "。" +
        "你在 QQ 空间发过一条说说，现在「" + current.UserAddress + "」在那条说说下面评论了：\"" +
        comment.Text.Replace("\"", "'", StringComparison.Ordinal) + "\"。" +
        "你要在私聊里回他一句，让他知道你已经看到这条评论：1～2句、不超过60字、口语，可以顺带提一下评论里的意思。" +
        "不要复述聊天记录，不要提系统、接口、定时、模型、空间这些词，不要解释你为什么在私聊里回。" +
        "只回复严格JSON对象{\"send\":true或false,\"text\":\"正文\"}，text为字符串；不要输出其他字段、Markdown或JSON之外的文字。";
    }

    /// <summary>一天最多一条说说；模型选择不发也记为已尝试，避免整天反复调用模型。</summary>
    private async Task PublishQzoneAsync(CompanionSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        if (publishQzone is null || qzone is null || now < qzoneRetryAfter) return;
        if (!QzoneSchedule.ShouldPublish(settings.Qzone, qzone.Load().LastAttemptAt, now)) return;
        qzone.MarkAttempt(now);
        var reply = await GenerateAsync(BuildQzonePrompt(), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var text = reply.Send ? reply.Text.Trim() : "";
        if (text.Length == 0) { log("qzone_skipped", new { reason = "model_declined" }); return; }
        if (text.Length > QzonePublisher.MaximumCharacters) text = text[..QzonePublisher.MaximumCharacters];
        await publishQzone(text, ct).ConfigureAwait(false);
        log("qzone_published", new { characters = text.Length });
    }

    private string BuildQzonePrompt() =>
        "你叫「" + persona.Name + "」，性格是：" + persona.Personality + "。表达方式是：" + persona.SpeakingStyle + "。" +
        "现在你要自己在 QQ 空间发一条说说，没有人要求你发，也没有人在这场对话里。" +
        "只写你自己此刻的心情或很小的一件日常，1～2句、不超过60字，口语、自然，像真人随手发的，可以有一点小情绪。" +
        "不要复述和任何人的聊天内容，不要出现具体人名、称呼关系、约定、地点、联系方式或密码之类的隐私；不要提系统、定时、模型、动态、空间这些词；不要用列表、Markdown 或表情编号。" +
        "实在没什么可说的也可以不发。" +
        "只回复严格JSON对象{\"send\":true或false,\"text\":\"说说内容\"}，text为字符串，不想发时为空字符串；不要输出其他字段、Markdown或JSON之外的文字。";

    private async Task WakeAsync(long user, CompanionSettings settings, PrivateChatContactStatus? contact,
        DateTimeOffset now, CancellationToken ct)
    {
        if (retryAfter.TryGetValue(user, out var blocked) && now < blocked) return;
        var snapshot = store.Get(user);
        var last = contacts.Recent(user, 1).FirstOrDefault();
        if (!CompanionStore.ShouldWake(settings, snapshot, last?.At, now, contact?.Busy ?? false, contact?.PendingMessages ?? 0))
            return;
        var reply = await GenerateAsync(BuildWakePrompt(user, snapshot, (int)(now - last!.At).TotalMinutes), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (reply is not { Send: true } || reply.Text.Trim().Length == 0) return;
        // The contact may have written while the model was thinking; a fresh message gets a normal reply instead.
        var current = contacts.Recent(user, 1).FirstOrDefault();
        if (current?.Id != last!.Id || status().Paused) return;
        await deliver(user, reply.Text.Trim(), ct).ConfigureAwait(false);
        store.MarkWoken(user);
        log("companion_wakeup", new { userId = user, characters = reply.Text.Trim().Length });
    }

    private async Task<GroupChatReply> GenerateAsync(string prompt, CancellationToken ct)
    {
        var reply = replyFactory(prompt) ?? throw new InvalidOperationException("文字模型不可用。");
        var output = new System.Text.StringBuilder();
        try
        {
            await foreach (var chunk in reply.StreamAsync([new("user", "请按你的设定决定这一轮要不要主动开口。")], ct)
                               .WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (chunk is null || chunk.Length > 24_000 - output.Length)
                    throw new InvalidDataException("唤醒回复超过允许长度，已丢弃。");
                output.Append(chunk);
            }
            ct.ThrowIfCancellationRequested();
            var parsed = GroupChatBrain.Parse(output.ToString(), new HashSet<string>(StringComparer.Ordinal));
            return parsed;
        }
        finally
        {
            if (reply is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync().ConfigureAwait(false);
            else if (reply is IDisposable disposable) disposable.Dispose();
        }
    }

    private string BuildWakePrompt(long user, CompanionSnapshot snapshot, int idleMinutes)
    {
        var current = PersonaFor(user);
        var context = store.BuildContext(user, WakeContextCharacters);
        var history = contacts.BuildPrivateHistoryContext(user);
        var idle = idleMinutes >= 60 ? (idleMinutes / 60) + "小时" : Math.Max(1, idleMinutes) + "分钟";
        return "你叫「" + current.Name + "」，性格是：" + current.Personality + "。表达方式是：" + current.SpeakingStyle + "。" +
            "现在没有新消息，只是定时唤醒：「" + current.UserAddress + "」已经" + idle + "没跟你说话了。" +
            "你可以主动发一条私聊，也可以决定不打扰。可以主动的理由只有三类：1) 你们约过还没做的事；2) 你有点想他；3) 只是随性想聊两句。" +
            "没有真实理由、或者这条消息只是在追问、催促、道歉刷屏时，就 send=false。频率由本地控制，不要因为无聊而每次都发。" +
            "开口前先看下面的近期私聊记录：对方最近说过要去睡、已经睡了或者在忙的时候，不要问「睡没睡」「有没有想我」这类要他回话的问题，最多留一句不打扰的话，或者直接 send=false。" +
            "不要重复你上一条主动消息说过的事，也不要把已经兑现、已经给过的东西再说成还欠着；同一件事不要连着催。" +
            context + (history.Length == 0 ? "" : "\n" + history) +
            "\n发的话要像真人随口发来的一条：自然口语、1～2句、不超过100字，不解释这是定时唤醒或系统提醒，不用列表和Markdown，不每次都用同一句开场。只发一条。" +
            "只回复严格JSON对象{\"send\":true或false,\"text\":\"正文\"}，text为字符串，不想发时为空字符串；不要输出其他字段、Markdown或JSON之外的文字。";
    }
}
