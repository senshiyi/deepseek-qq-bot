using System.Globalization;
using System.Text.Json;
using AudioBridge;

/// <summary>拿真实的 OneBot 事件 JSON 验证图片收件箱与关联；只读本地文件，不下载图片、不发消息。</summary>
public static class ImagePayloadProbe
{
    public static int Run(string file)
    {
        var root = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        if (root.TryGetProperty("data", out var data)) root = data;
        var now = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("time").GetInt64());
        var group = root.GetProperty("group_id").GetInt64().ToString(CultureInfo.InvariantCulture);
        var self = root.GetProperty("self_id").GetInt64().ToString(CultureInfo.InvariantCulture);
        var inbox = new GroupImageInbox(group, self);
        var accepted = inbox.AddEvent(root, now.AddSeconds(1));
        var resolution = inbox.Resolve(group, now.AddSeconds(5), TimeSpan.FromMinutes(3));
        Console.WriteLine("accepted=" + accepted + " count=" + inbox.Count + " kind=" + resolution.Kind +
            " entries=" + resolution.Entries.Count);
        foreach (var entry in resolution.Entries)
            Console.WriteLine("  message=" + entry.MessageId + " sender=" + entry.SenderId + " host=" + entry.Url.Host);
        return accepted ? 0 : 1;
    }
}
