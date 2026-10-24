using System.Text.Json;

namespace AudioBridge;

public sealed record BotSettings
{
    public string ModelDirectory { get; init; } = "Models/sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23";
    public string Name { get; init; } = "小深";
    public string Voice { get; init; } = "Microsoft Huihui Desktop";
    public int SpeechRate { get; init; }
    public double ApiBudgetYuan { get; init; } = 0.30;
    public int MaxApiCalls { get; init; } = 10;
    public bool LogTranscripts { get; init; }
    public bool ImageEventsEnabled { get; init; }
    public int OneBotPort { get; init; } = 17800;
    public string? OneBotSelfId { get; init; }
    public string FixedReply { get; init; } = "我听到了。现在是本地语音通路测试。";

    public static BotSettings Load(string path)
    {
        var settings = File.Exists(path)
            ? JsonSerializer.Deserialize<BotSettings>(File.ReadAllText(path), BridgeConfig.JsonOptions)
                ?? throw new ArgumentException("机器人配置为空。")
            : new BotSettings();
        if (string.IsNullOrWhiteSpace(settings.ModelDirectory) || string.IsNullOrWhiteSpace(settings.Name) ||
            settings.Name.Length > 12 || settings.SpeechRate is < -3 or > 3 ||
            !double.IsFinite(settings.ApiBudgetYuan) || settings.ApiBudgetYuan is <= 0 or > 0.30 ||
            settings.MaxApiCalls is < 1 or > 10 || settings.OneBotPort is < 1024 or > 65535)
            throw new ArgumentException("机器人配置无效；本轮 API 累计上限为0.30元，最多10次，语速范围-3到3。");
        return settings;
    }

    // Read only named project secrets. Never execute or log .env text.
    public static string? LoadApiKey(string projectDirectory) => LoadSecret(projectDirectory, "DEEPSEEK_API_KEY");

    public static string? LoadSecret(string projectDirectory, string name)
    {
        if (name is not ("DEEPSEEK_API_KEY" or "ONEBOT_ACCESS_TOKEN" or "SCHOOL_API_KEY")) throw new ArgumentException("未支持的本地配置项。");
        var key = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(key)) return key;
        var path = Path.Combine(projectDirectory, ".env");
        if (!File.Exists(path)) return null;
        foreach (var line in File.ReadLines(path))
        {
            var value = line.Trim();
            if (!value.StartsWith(name + "=", StringComparison.Ordinal)) continue;
            value = value[(name.Length + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        return null;
    }
}
