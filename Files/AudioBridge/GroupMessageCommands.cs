using System.Globalization;
using System.Text.Json;

namespace AudioBridge;

public static class GroupMessageCommands
{
    public static async Task RunAsync(string command, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var project = Environment.CurrentDirectory;
        var memes = new MemeLibrary(project);
        if (command == "memes")
        { Write(new { command, items = memes.List(), scope = "local curated library; no QQ send" }); return; }
        if (command == "meme-import")
        {
            var imported = memes.Import(Required(options, "id"), Required(options, "file"), Required(options, "tags").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            Write(new { command, imported.Id, imported.Tags, imported.MediaType, bytes = imported.Content.Length }); return;
        }
        var settings = GroupChatSettings.Load(options.GetValueOrDefault("chat-config", "group-chat.local.json"));
        if (options.TryGetValue("mode", out var mode)) settings = settings with { Mode = mode };
        settings.Validate();
        if (command is "school-check" or "school-ai-check")
        {
            var school = SchoolModelSettings.Load(Path.Combine(project, "school-api.local.json"));
            var key = BotSettings.LoadSecret(project, "SCHOOL_API_KEY");
            var budget = new SchoolUsageBudget(Path.Combine(project, "Results/local/school-api-budget.json"), school);
            if (command == "school-check")
            {
                Write(new { command, school.Model, school.BaseUrl, school.Stream, keyConfigured = !string.IsNullOrWhiteSpace(key),
                    budget = budget.Snapshot(), scope = "local configuration only; no HTTP, QQ send or audio" }); return;
            }
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("请在本地.env填写SCHOOL_API_KEY。");
            var list = memes.List();
            var prompt = GroupChatBrain.SystemPrompt(list.Select(m => (m.Id, m.Tags)), settings.ParsedMode());
            var brain = new GroupChatBrain(_ => new SchoolModelReply(school, key, budget, prompt), list.Select(m => m.Id));
            var text = options.GetValueOrDefault("text", "请简单回复已收到这条功能测试，并选一张表示收到的表情包。");
            var started = System.Diagnostics.Stopwatch.StartNew();
            var result = await brain.GenerateAsync([new(settings.AllowedGroupIds[0], 12345, 1, DateTimeOffset.UtcNow, text, true, false)], ct);
            Write(new { command, school.Model, result, elapsedMilliseconds = started.Elapsed.TotalMilliseconds,
                budget = budget.Snapshot(), scope = "real school model JSON decision only; no QQ send or audio" }); return;
        }
        if (command == "chat-ai-check")
        {
            var key = BotSettings.LoadApiKey(project) ?? throw new InvalidOperationException("请在本地.env配置DEEPSEEK_API_KEY。");
            var budget = new ApiBudget(Path.Combine(project, "Results/local/deepseek-budget.json"), .30, 10);
            var list = memes.List();
            var prompt = GroupChatBrain.SystemPrompt(list.Select(m => (m.Id, m.Tags)), settings.ParsedMode());
            var brain = new GroupChatBrain(_ => new DeepSeekReply(key, budget, systemPrompt: prompt, jsonMode: true), list.Select(m => m.Id));
            var text = options.GetValueOrDefault("text", "请简单回复已收到这条功能测试，并选一张表示收到的表情包。");
            var result = await brain.GenerateAsync([new(settings.AllowedGroupIds[0], 12345, 1, DateTimeOffset.UtcNow, text, true, false)], ct);
            Write(new { command, result, budget = budget.Snapshot(), scope = "real DeepSeek JSON decision only; no QQ login, send or audio" }); return;
        }
        var live = ReadLive(options);
        if (command == "chat")
        {
            var seconds = options.TryGetValue("seconds", out var duration) ? double.Parse(duration, CultureInfo.InvariantCulture) : 600;
            await ChatApplicationHost.RunAsync(settings, Required(options, "group"), seconds, live,
                options.GetValueOrDefault("engine", live ? settings.Engine : "fixed"), ct);
            return;
        }
        var token = BotSettings.LoadSecret(project, "ONEBOT_ACCESS_TOKEN");
        using var client = new OneBotMessageClient(settings.OneBotBaseUrl,
            token ?? (live || command == "chat-check" ? throw new InvalidOperationException("请先配置ONEBOT_ACCESS_TOKEN。") : "preview-no-network"),
            settings.AllowedGroupIds.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        if (command == "chat-check")
        {
            var status = await client.GetStatusAsync(ct);
            if (!status.Online || !status.Good) throw new InvalidOperationException("NapCat接口可达，但QQ当前离线或连接异常；未启动新消息会话。");
            var self = await client.GetLoginInfoAsync(ct);
            if (settings.SelfId > 0 && self != settings.SelfId.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("已登录QQ账号不符合配置selfId，本次不发送。");
            var configuredSchool = settings.Engine == "school" ? SchoolModelSettings.Load(Path.Combine(project, "school-api.local.json")) : null;
            Write(new { command, online = status.Online, good = status.Good, loggedInSelfId = self, settings.Mode, settings.Engine, settings.AllowedGroupIds,
                settings.AllowedPrivateUserIds, settings.PrivateIntervalSeconds, settings.PrivatePerHour,
                model = configuredSchool?.Model ?? "deepseek-flash",
                apiBaseUrl = configuredSchool?.BaseUrl ?? "https://api.deepseek.com/",
                keyEnvironment = configuredSchool is null ? "DEEPSEEK_API_KEY" : "SCHOOL_API_KEY",
                engineScope = "configured default; running window S shows the actual selected engine",
                events = $"http://127.0.0.1:{settings.EventPort}/onebot/", memes = memes.List().Count, voiceStarted = false }); return;
        }
        if (command != "chat-send") throw new ArgumentException("未知群消息命令。");
        var groupId = Required(options, "group");
        if (!long.TryParse(groupId, NumberStyles.None, CultureInfo.InvariantCulture, out var group) || !settings.AllowedGroupIds.Contains(group))
            throw new ArgumentException("目标群不在群消息白名单内。");
        var segments = new List<OneBotSegment>();
        var textBody = options.GetValueOrDefault("text", "");
        if (textBody.Length > OneBotMessageClient.MaximumTextCharacters) throw new ArgumentException("手动文字最多3000字符。");
        if (textBody.Length > 0) segments.Add(OneBotSegment.Text(textBody));
        if (options.TryGetValue("face", out var face)) segments.Add(OneBotSegment.Face(int.Parse(face, CultureInfo.InvariantCulture)));
        var memeId = options.GetValueOrDefault("meme");
        if (memeId is not null) segments.Add(memes.ResolveSegment(memeId));
        if (segments.Count == 0) throw new ArgumentException("请提供text、face或meme。");
        if (!live)
        {
            Write(new { command, live = false, group, text = textBody, face = options.GetValueOrDefault("face"), meme = memeId,
                scope = "preview only; no HTTP send, no DeepSeek call, no audio" }); return;
        }
        var account = await client.GetLoginInfoAsync(ct);
        if (settings.SelfId > 0 && account != settings.SelfId.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("已登录账号与selfId不同，拒绝发送。");
        var sent = await client.SendGroupAsync(new(groupId, segments), ct);
        Write(new { command, live = true, sent.MessageId, group, status = "OneBot API accepted; verify actual display in QQ" });
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("缺少--" + name);
    public static bool ReadLive(IReadOnlyDictionary<string, string> options) =>
        !options.TryGetValue("live", out var live) ? false : bool.TryParse(live, out var value) ? value : throw new ArgumentException("live必须为true或false。");
    private static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value, BridgeConfig.JsonOptions));
}
