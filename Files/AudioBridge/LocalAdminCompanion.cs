using System.Net;
using System.Text.Json;

namespace AudioBridge;

/// <summary>心脑（心情与日记）面板接口：只暴露本机已保存的状态，不提供任意文件路径或模型调用。</summary>
public sealed partial class LocalAdminServer
{
    private readonly CompanionStore companion;

    private object CompanionList() => new
    {
        settings = companion.LoadSettings(),
        contacts = companion.All().Select(item => new
        {
            item.UserId, item.Mood, item.MoodUpdatedAt, item.LastAuditAt, item.LastWakeupAt, item.WakeupsToday,
            entries = item.Entries.Select(entry => new
            {
                entry.Id, entry.Level, levelLabel = DiaryLevels.Label(entry.Level), entry.Text, entry.Mood,
                entry.Unsaid, entry.CreatedAt, entry.ExpiresAt
            })
        })
    };

    private async Task DeleteCompanionEntryAsync(HttpListenerContext context, long userId, string id, CancellationToken ct)
    {
        var deleted = companion.DeleteEntry(userId, id);
        await JsonAsync(context.Response, new { deleted }, 200, ct);
    }

    private async Task SaveCompanionSettingsAsync(HttpListenerContext context, JsonElement body, CancellationToken ct)
    {
        var value = Deserialize<CompanionSettings>(body);
        value.Validate();
        companion.SaveSettings(value);
        await JsonAsync(context.Response, value, 200, ct);
    }
}
