using System.Threading.Channels;

namespace AudioBridge;

public static class ChatApplicationHost
{
    public static async Task RunAsync(GroupChatSettings initial, string groupId, double seconds, bool live, string engine, CancellationToken ct,
        int adminPort = LocalAdminServer.DefaultPort)
    {
        if (!double.IsFinite(seconds) || seconds != 0 && seconds is < 10 or > 86400) throw new ArgumentException("chat seconds为0或10～86400秒。");
        var project = Environment.CurrentDirectory;
        var profiles = new BotProfileStore(project);
        var contacts = new ContactContextStore(project);
        ScheduledMessageStore? scheduledMessages = null;
        var scheduledRateLimits = new ScheduledMessageRateLimits();
        ScheduledMessageRuntime? scheduledRuntime = null;
        // Legacy selfId=0 sessions resolve their account in the runtime. They can still chat,
        // but manual image import requires the explicit account binding used by the admin API.
        var receivedImages = initial.SelfId > 0 ? new ReceivedImageInbox(initial.SelfId) : null;
        var runtime = new AdminRuntimeControl();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (seconds > 0) lifetime.CancelAfter(TimeSpan.FromSeconds(seconds));
        var reload = Channel.CreateBounded<GroupChatSettings>(new BoundedChannelOptions(1)
            { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
        string? hostError = null;
        Task? delayedStop = null;
        var selectedEngine = engine;
        var currentSettings = initial;
        IStreamingReply PersonaReply(string prompt)
        {
            var activeEngine = Volatile.Read(ref selectedEngine);
            var local = Path.Combine(project, "Results", "local");
            if (activeEngine == "school")
            {
                var currentSchool = SchoolModelSettings.Load(Path.Combine(project, "school-api.local.json"));
                var secret = BotSettings.LoadSecret(project, "SCHOOL_API_KEY") ?? throw new InvalidOperationException("学校密钥未配置。");
                return new SchoolModelReply(currentSchool, secret,
                    new SchoolUsageBudget(Path.Combine(local, "school-api-budget.json"), currentSchool), prompt, maximumOutputTokens: 2048);
            }
            if (activeEngine == "deepseek")
                return new DeepSeekReply(BotSettings.LoadApiKey(project) ?? throw new InvalidOperationException("DeepSeek密钥未配置。"),
                    new ApiBudget(Path.Combine(local, "deepseek-budget.json"), currentSettings.ApiBudgetYuan, currentSettings.MaxApiCalls),
                    systemPrompt: prompt, jsonMode: true, maximumOutputTokens: 2048);
            throw new InvalidOperationException("当前预览引擎不调用模型，请先选择已配置的文字模型。");
        }
        var admin = new LocalAdminServer(project, LocalAdminServer.LoadOrCreateToken(project),
            () => hostError is null ? runtime.Snapshot() : new { running = false, paused = true, lastError = hostError }, async action =>
            {
                switch (action)
                {
                    case "pause": await runtime.SetPausedAsync(true); break;
                    case "resume": await runtime.SetPausedAsync(false); break;
                    case "apply":
                        var next = GroupChatSettings.Load(Path.Combine(project, "group-chat.local.json"));
                        var secretName = next.Engine == "school" ? "SCHOOL_API_KEY" : "DEEPSEEK_API_KEY";
                        if (live && string.IsNullOrWhiteSpace(BotSettings.LoadSecret(project, secretName)))
                            throw new InvalidOperationException("所选引擎尚未配置密钥。");
                        _ = profiles.GetPersona();
                        if (!reload.Writer.TryWrite(next)) throw new InvalidOperationException("消息宿主正在停止。");
                        break;
                    case "stop":
                        delayedStop = Task.Run(async () => { await Task.Delay(150); await lifetime.CancelAsync(); });
                        break;
                }
            }, adminPort, receivedImages: receivedImages, scheduledMessages: scheduledMessages,
            scheduleChanged: id => Volatile.Read(ref scheduledRuntime)?.Invalidate(id), personaReplyFactory: PersonaReply);
        var panelTask = admin.RunAsync(lifetime.Token);
        var maintenance = PurgeHistoryAsync(contacts, lifetime.Token);
        var settings = initial;
        var selectedGroup = groupId;
        Console.WriteLine($"本地管理面板：{admin.Prefix}；双击 open-panel.cmd 自动登录。电脑麦克风/音箱语音联调保持暂停；QQ 语音消息按 voice.local.json 发送。");
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (panelTask.IsFaulted)
                {
                    // Management failure must remain visible and must never start another message session.
                    await panelTask;
                }
                hostError = null;
                using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var nextSettings = reload.Reader.ReadAsync(sessionStop.Token).AsTask();
                Task? session = null;
                try
                {
                    var persona = profiles.GetPersona();
                    try { scheduledMessages ??= new ScheduledMessageStore(project); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
                    { Console.WriteLine("定时消息存储暂不可用；聊天继续，修复文件后应用设置可重试。错误类型：" + error.GetType().Name); }
                    receivedImages?.Clear();
                    var active = settings with { BotNames = new[] { persona.Name }.Concat(settings.BotNames).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray() };
                    session = GroupChatRuntime.RunAsync(active, selectedGroup, 0, live, selectedEngine,
                        sessionStop.Token, control: runtime, profiles: profiles, contacts: contacts, persona: persona,
                        receivedImages: receivedImages, scheduledMessages: scheduledMessages,
                        scheduleBinding: current => Volatile.Write(ref scheduledRuntime, current), scheduledRateLimits: scheduledRateLimits);
                    var done = await Task.WhenAny(session, nextSettings, panelTask);
                    if (done == panelTask) { await panelTask; break; }
                    if (done == nextSettings)
                    {
                        settings = await nextSettings;
                        currentSettings = settings;
                        await sessionStop.CancelAsync();
                        try { await session; } catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
                        selectedEngine = settings.Engine;
                        var currentGroup = long.TryParse(selectedGroup, out var previousGroup) && settings.AllowedGroupIds.Contains(previousGroup)
                            ? previousGroup : settings.AllowedGroupIds[0];
                        selectedGroup = (settings.ActiveGroupId != 0 ? settings.ActiveGroupId : currentGroup).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        admin.Applied();
                        continue;
                    }
                    await session;
                    break;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    if (panelTask.IsFaulted) throw;
                    hostError = error.GetType().Name;
                    runtime.Clear();
                    Console.WriteLine("消息会话未启动或已失败；面板保留，可修正配置后应用。错误类型：" + hostError);
                    if (await Task.WhenAny(nextSettings, panelTask) == panelTask) { await panelTask; break; }
                    settings = await nextSettings;
                    currentSettings = settings;
                    selectedEngine = settings.Engine;
                    selectedGroup = (settings.ActiveGroupId != 0 ? settings.ActiveGroupId : settings.AllowedGroupIds[0]).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    continue;
                }
                finally
                {
                    await sessionStop.CancelAsync();
                    if (session is not null) try { await session; } catch (Exception) { /* Observed above; release before next owner. */ }
                    try { await nextSettings; } catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync(); reload.Writer.TryComplete(); runtime.Clear();
            receivedImages?.Clear();
            try { await panelTask; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            await maintenance;
            if (delayedStop is not null) await delayedStop;
        }
    }

    private static async Task PurgeHistoryAsync(ContactContextStore contacts, CancellationToken ct)
    {
        try
        {
            do
            {
                try
                {
                    var result = contacts.PurgeExpiredHistory();
                    if (result.FailedFiles > 0) Console.WriteLine("私聊原文清理：部分本地文件无法处理，保留原文件；请检查本地存储。文件数：" + result.FailedFiles);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or InvalidDataException)
                { Console.WriteLine("私聊原文清理暂不可用：" + error.GetType().Name); }
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            } while (!ct.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
