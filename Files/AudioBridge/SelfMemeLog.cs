using System.Text.Json;

namespace AudioBridge;

/// <summary>机器人自己收藏表情包的每日额度（默认每天 40 张）；只记录本机状态，不影响人工在面板添加。</summary>
public sealed class SelfMemeLog
{
    private readonly object gate = new();
    private readonly string path;
    public int DailyLimit { get; }

    public SelfMemeLog(string path, int dailyLimit = 40)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (dailyLimit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(dailyLimit), "每日自动收藏上限必须为 1～200。");
        this.path = Path.GetFullPath(path);
        DailyLimit = dailyLimit;
    }

    public int UsedToday(DateTimeOffset now) => Load(now).Count;

    /// <summary>占一个今天的名额；名额用完或状态不可写时返回 false，调用方不得继续保存。</summary>
    public bool TryConsume(DateTimeOffset now)
    {
        lock (gate)
        {
            var current = Load(now);
            if (current.Count >= DailyLimit) return false;
            Save(current with { Count = current.Count + 1 });
            return true;
        }
    }

    private DayCount Load(DateTimeOffset now)
    {
        var day = now.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            if (!File.Exists(path)) return new(day, 0);
            var value = JsonSerializer.Deserialize<DayCount>(File.ReadAllText(path), BridgeConfig.JsonOptions);
            return value is null || value.Day != day ? new(day, 0) : new(day, Math.Max(0, value.Count));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new(day, 0); }
    }

    private void Save(DayCount value)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, BridgeConfig.JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public sealed record DayCount(string Day, int Count);
}
