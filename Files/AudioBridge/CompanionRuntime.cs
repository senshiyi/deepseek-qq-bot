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
    private readonly Dictionary<long, DateTimeOffset> retryAfter = [];
    private DateTimeOffset nextPurge = DateTimeOffset.MinValue;

    public CompanionRuntime(CompanionStore store, ContactContextStore contacts, PersonaSettings persona,
        Func<string, IStreamingReply> replyFactory, Func<GroupChatStatus> status,
        Func<long, string, CancellationToken, Task> deliver, IReadOnlyList<long> allowedUsers, bool live,
        Action<string, object> log, TimeProvider? timeProvider = null)
    {
        this.store = store; this.contacts = contacts; this.persona = persona; this.replyFactory = replyFactory;
        this.status = status; this.deliver = deliver; this.allowedUsers = allowedUsers; this.live = live;
        this.log = log; this.timeProvider = timeProvider ?? TimeProvider.System;
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
        var now = timeProvider.GetUtcNow();
        CompanionSettings settings;
        try { settings = store.LoadSettings(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            log("companion_settings_unavailable", new { type = error.GetType().Name });
            return;
        }
        if (!live) return;
        var state = status();
        if (state.Paused) return;
        if (now >= nextPurge)
        {
            try
            {
                var removed = store.PurgeExpired();
                if (removed > 0) log("companion_diary_forgotten", new { removed });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or TimeoutException)
            { log("companion_forget_failed", new { type = error.GetType().Name }); }
            nextPurge = now + PurgeInterval;
        }
        foreach (var user in allowedUsers.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var contact = state.PrivateContacts.FirstOrDefault(item => item.UserId == user);
            try { await AuditAsync(user, settings, now, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or InvalidOperationException or TimeoutException)
            { retryAfter[user] = now + RetryDelay; log("companion_diary_failed", new { userId = user, type = error.GetType().Name }); }
            try { await WakeAsync(user, settings, contact, now, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or InvalidOperationException or TimeoutException or OneBotMessageException)
            { retryAfter[user] = now + RetryDelay; log("companion_wakeup_failed", new { userId = user, type = error.GetType().Name }); }
        }
    }

    /// <summary>每天一次：把这天的新私聊整理成日记和心情；没有可记的内容也记为已整理，避免反复调用。</summary>
    private async Task AuditAsync(long user, CompanionSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        if (!settings.Audit) return;
        if (retryAfter.TryGetValue(user, out var blocked) && now < blocked) return;
        var snapshot = store.Get(user);
        var boundary = CompanionStore.AuditBoundary(now, settings.AuditHour);
        if (snapshot.LastAuditAt is { } lastAudit && lastAudit >= boundary) return;
        var from = snapshot.LastAuditAt ?? now.AddDays(-1);
        var history = contacts.Recent(user, 200).Where(item => item.At > from).ToArray();
        if (history.Length == 0) { store.MarkAudited(user, now); return; }
        var draft = await CompanionJournal.GenerateAsync(persona, snapshot.Mood, history, replyFactory, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var written = 0;
        foreach (var entry in draft.Entries)
        {
            // New writes are stamped with the audit time so forgetting stays measurable from now.
            store.AddEntry(user, entry.Level, entry.Text, entry.Mood, entry.Unsaid);
            written++;
        }
        if (draft.Mood.Length > 0) store.SetMood(user, draft.Mood);
        store.MarkAudited(user, now);
        log("companion_diary", new { userId = user, entries = written, moodUpdated = draft.Mood.Length > 0,
            until = boundary.AddDays(1) });
    }

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
        if (current?.At != last!.At || status().Paused) return;
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
        var context = store.BuildContext(user, WakeContextCharacters);
        var history = contacts.BuildPrivateHistoryContext(user);
        var idle = idleMinutes >= 60 ? (idleMinutes / 60) + "小时" : Math.Max(1, idleMinutes) + "分钟";
        return "你叫「" + persona.Name + "」，性格是：" + persona.Personality + "。表达方式是：" + persona.SpeakingStyle + "。" +
            "现在没有新消息，只是定时唤醒：「" + persona.UserAddress + "」已经" + idle + "没跟你说话了。" +
            "你可以主动发一条私聊，也可以决定不打扰。可以主动的理由只有三类：1) 你们约过还没做的事；2) 你有点想他；3) 只是随性想聊两句。" +
            "没有真实理由、或者这条消息只是在追问、催促、道歉刷屏时，就 send=false。频率由本地控制，不要因为无聊而每次都发。" +
            context + (history.Length == 0 ? "" : "\n" + history) +
            "\n发的话要像真人随口发来的一条：自然口语、1～2句、不超过100字，不解释这是定时唤醒或系统提醒，不用列表和Markdown，不每次都用同一句开场。只发一条。" +
            "只回复严格JSON对象{\"send\":true或false,\"text\":\"正文\"}，text为字符串，不想发时为空字符串；不要输出其他字段、Markdown或JSON之外的文字。";
    }
}
