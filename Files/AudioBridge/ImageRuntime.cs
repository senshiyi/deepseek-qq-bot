using System.Text.Json;

namespace AudioBridge;

public static class ImageRuntime
{
    public static async Task RunAsync(BotSettings settings, string group, double seconds, CancellationToken ct)
    {
        if (!double.IsFinite(seconds) || seconds is < 5 or > 300) throw new ArgumentException("图片入口seconds为5～300秒。");
        var token = BotSettings.LoadSecret(Environment.CurrentDirectory, "ONEBOT_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("请先在本地.env或环境变量配置ONEBOT_ACCESS_TOKEN。");
        Environment.SetEnvironmentVariable("ONEBOT_ACCESS_TOKEN", token, EnvironmentVariableTarget.Process);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stopping.CancelAfter(TimeSpan.FromSeconds(seconds));
        var inbox = new GroupImageInbox(group, settings.OneBotSelfId, settings.OneBotPort);
        var listening = inbox.StartAsync(stopping.Token);
        try
        {
            while (!stopping.IsCancellationRequested && !listening.IsCompleted)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { command = "images", local = DateTimeOffset.Now,
                    inbox.IsRunning, inbox.Count, inbox.LastError, prefix = inbox.Prefix,
                    scope = "Image metadata only. No QQ login, voice capture, image download, or API call." }, BridgeConfig.JsonOptions));
                await Task.Delay(2000, stopping.Token);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally { stopping.Cancel(); await listening; }
        if (inbox.LastError is not null) throw new IOException(inbox.LastError);
    }
}
