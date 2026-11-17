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
            item.Affection, item.Possession,
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

    /// <summary>人工调好感度（-1000～1000）/占有欲（0～100），越界拒绝。</summary>
    private async Task SaveCompanionMetersAsync(HttpListenerContext context, long userId, JsonElement body, CancellationToken ct)
    {
        var affection = body.GetProperty("affection").GetInt32();
        var possession = body.GetProperty("possession").GetInt32();
        companion.SetMeters(userId, affection, possession);
        var snapshot = companion.Get(userId);
        await JsonAsync(context.Response, new { snapshot.UserId, snapshot.Affection, snapshot.Possession }, 200, ct);
    }

    private async Task SaveCompanionSettingsAsync(HttpListenerContext context, JsonElement body, CancellationToken ct)
    {
        var value = Deserialize<CompanionSettings>(body);
        value.Validate();
        companion.SaveSettings(value);
        await JsonAsync(context.Response, value, 200, ct);
    }
}
