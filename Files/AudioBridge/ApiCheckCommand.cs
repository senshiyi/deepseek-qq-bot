using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AudioBridge;

public static class ApiCheckCommand
{
    public static async Task RunAsync(BotSettings settings, CancellationToken ct)
    {
        var project = Environment.CurrentDirectory;
        var key = BotSettings.LoadApiKey(project) ?? throw new InvalidOperationException("请先在本地.env配置DEEPSEEK_API_KEY。");
        var local = Path.Combine(project, "Results", "local");
        Directory.CreateDirectory(local);
        using var sessionLock = new FileStream(Path.Combine(local, "bot-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var budget = new ApiBudget(Path.Combine(local, "deepseek-budget.json"), settings.ApiBudgetYuan, settings.MaxApiCalls);
        using var reply = new DeepSeekReply(key, budget);
        var watch = Stopwatch.StartNew();
        double? first = null;
        var content = new StringBuilder();
        await foreach (var text in reply.StreamAsync([new("user", "这是一次API连通测试。请只回复：语音助手已就绪。")], ct))
        {
            first ??= watch.Elapsed.TotalMilliseconds;
            content.Append(text);
            if (content.Length > 2048) throw new IOException("测试回复超过上限。");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { command = "api-check", local = DateTimeOffset.Now,
            requestedModel = "deepseek-flash", observedModel = reply.ObservedModel, reply = content.ToString(),
            firstTextMilliseconds = first, completedMilliseconds = watch.Elapsed.TotalMilliseconds,
            budget = budget.Snapshot(), scope = "Real formal API text-only synthetic prompt. No QQ capture, no playback, no images." }, BridgeConfig.JsonOptions));
    }
}
