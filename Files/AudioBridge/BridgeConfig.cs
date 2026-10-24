using System.Text.Json;
using System.Text.Encodings.Web;

namespace AudioBridge;

public sealed record BridgeConfig
{
    public int QqProcessId { get; init; }
    public string? OutputDeviceId { get; init; }
    public double Volume { get; init; } = 0.12;
    public int ToneMilliseconds { get; init; } = 800;

    public void Validate(bool requireRoutes = true)
    {
        if (!double.IsFinite(Volume) || Volume < 0 || Volume > 0.5)
            throw new ArgumentException("volume 必须为 0～0.5，建议从 0.12 开始。");
        if (ToneMilliseconds is < 100 or > 5000)
            throw new ArgumentException("toneMilliseconds 必须为 100～5000。");
        if (requireRoutes && (QqProcessId <= 0 || string.IsNullOrWhiteSpace(OutputDeviceId)))
            throw new ArgumentException("请先选择 QQ 进程和输出设备：configure --pid 数字 --output \"设备ID\"。");
    }

    public static BridgeConfig Load(string path) =>
        JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(path), JsonOptions)
        ?? throw new ArgumentException("配置文件为空。");

    public void Save(string path)
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + Environment.NewLine);
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };
}
