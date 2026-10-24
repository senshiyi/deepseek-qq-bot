using System.Text.Json;

namespace AudioBridge;

public sealed record GroupChatSettings
{
    public const int MinimumIntervalSeconds = 3;
    public const int MaximumIntervalSeconds = 3600;
    public const int MaximumPerHour = 120;

    public string OneBotBaseUrl { get; init; } = "http://127.0.0.1:3000/";
    public int EventPort { get; init; } = 17801;
    public long SelfId { get; init; }
    public long[] AllowedGroupIds { get; init; } = [];
    public long[] AllowedPrivateUserIds { get; init; } = [];
    public int PrivateIntervalSeconds { get; init; } = 8;
    public int PrivatePerHour { get; init; } = 30;
    public int MentionIntervalSeconds { get; init; } = 8;
    public int MentionPerHour { get; init; } = 30;
    public int ActiveIntervalSeconds { get; init; } = 120;
    public int ActivePerHour { get; init; } = 6;
    public int ActiveMessageEvery { get; init; } = 3;
    public long ActiveGroupId { get; init; }
    public string[] BotNames { get; init; } = ["小深", "机器人"];
    public string Mode { get; init; } = "mention";
    public string Engine { get; init; } = "deepseek";
    public string MemeDirectory { get; init; } = "Files/Memes";

    public static GroupChatSettings Load(string path)
    {
        if (!File.Exists(path)) throw new ArgumentException("缺少群消息配置；请将Files/group-chat-config.example.json复制为group-chat.local.json并填写群白名单。");
        var value = JsonSerializer.Deserialize<GroupChatSettings>(File.ReadAllText(path), BridgeConfig.JsonOptions)
            ?? throw new ArgumentException("群消息配置为空。");
        value.Validate();
        return value;
    }

    public GroupChatMode ParsedMode() => Mode.ToLowerInvariant() switch
    {
        "mention" => GroupChatMode.Mention, "active" => GroupChatMode.Active, "manual" => GroupChatMode.Manual,
        _ => throw new ArgumentException("群消息mode必须为mention、active或manual。")
    };

    public void Validate()
    {
        if (SelfId < 0 || AllowedGroupIds is null || AllowedGroupIds.Length is < 1 or > 8 || AllowedGroupIds.Any(x => x <= 0) ||
            AllowedGroupIds.Distinct().Count() != AllowedGroupIds.Length || EventPort is < 1024 or > 65535 ||
            AllowedPrivateUserIds is null || AllowedPrivateUserIds.Length > 16 ||
            AllowedPrivateUserIds.Any(x => x <= 0 || x == SelfId) ||
            AllowedPrivateUserIds.Distinct().Count() != AllowedPrivateUserIds.Length ||
            PrivateIntervalSeconds is < MinimumIntervalSeconds or > MaximumIntervalSeconds || PrivatePerHour is < 1 or > MaximumPerHour ||
            MentionIntervalSeconds is < MinimumIntervalSeconds or > MaximumIntervalSeconds || MentionPerHour is < 1 or > MaximumPerHour ||
            ActiveIntervalSeconds is < MinimumIntervalSeconds or > MaximumIntervalSeconds || ActivePerHour is < 1 or > MaximumPerHour ||
            ActiveMessageEvery is < 1 or > 20 ||
            ActiveGroupId != 0 && !AllowedGroupIds.Contains(ActiveGroupId) ||
            BotNames is null || BotNames.Length is < 1 or > 4 || BotNames.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 16) ||
            !string.Equals(MemeDirectory, "Files/Memes", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("消息配置无效：需1～8个群号，私聊白名单最多16人且不含自身；各类回复间隔为3～3600秒、每小时上限为1～120次；主动机会每1～20条可用普通文字一次；表情库限定Files/Memes。");
        if (Engine is not ("deepseek" or "school")) throw new ArgumentException("群聊默认engine为deepseek或school。");
        _ = ParsedMode();
    }
}
