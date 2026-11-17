using System.Net;
using System.Text.Json;

namespace AudioBridge;

public sealed partial class LocalAdminServer
{
    private readonly Func<string, IStreamingReply>? personaReplyFactory;
    private readonly SemaphoreSlim personaGeneration = new(1, 1);
    private readonly object personaDraftGate = new();
    private readonly Dictionary<string, PersonaLearningDraft> personaDrafts = new(StringComparer.Ordinal);

    private void RequireLearningContact(long userId)
    {
        var settings = GroupChatSettings.Load(Path.Combine(project, "group-chat.local.json"));
        if (!settings.AllowedPrivateUserIds.Contains(userId))
            throw new ArgumentException("人格学习来源必须是现有私聊白名单联系人。");
    }

    private void PurgePersonaDrafts()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
        foreach (var id in personaDrafts.Where(pair => pair.Value.GeneratedAt < cutoff).Select(pair => pair.Key).ToArray())
            personaDrafts.Remove(id);
    }

    private async Task PersonaLearningAsync(HttpListenerContext context, string route, JsonElement body, CancellationToken ct)
    {
        if (route == "/api/persona-learning/generate")
        {
            if (personaReplyFactory is null)
            { await JsonAsync(context.Response, new { error = "当前宿主未接入人格学习模型。" }, 503, ct); return; }
            var userId = body.GetProperty("userId").GetInt64();
            RequireLearningContact(userId);
            if (!await personaGeneration.WaitAsync(0, ct))
            { await JsonAsync(context.Response, new { error = "已有一项人格草稿正在生成，请等待完成。" }, 409, ct); return; }
            try
            {
                // 草稿基于该联系人此刻实际使用的人格；确认后写入他的专属人格，不动全局角色。
                var current = profiles.EffectivePersona(userId);
                var history = contacts.Recent(userId, 1000);
                var draft = await PersonaLearning.GenerateAsync(current, userId, history, personaReplyFactory, ct);
                ct.ThrowIfCancellationRequested();
                // The allowlist may have changed while the external request was in flight.
                RequireLearningContact(userId);
                var id = Guid.NewGuid().ToString("N");
                lock (personaDraftGate)
                {
                    PurgePersonaDrafts();
                    if (personaDrafts.Count >= 8)
                        personaDrafts.Remove(personaDrafts.MinBy(pair => pair.Value.GeneratedAt).Key);
                    personaDrafts.Add(id, draft);
                }
                await JsonAsync(context.Response, new { draftId = id, draft, expiresAt = draft.GeneratedAt.AddMinutes(30) }, 200, ct);
            }
            finally { personaGeneration.Release(); }
            return;
        }
        if (route == "/api/persona-learning/confirm")
        {
            var id = body.GetProperty("draftId").GetString() ?? "";
            PersonaSettings value;
            bool saved;
            long target;
            lock (personaDraftGate)
            {
                PurgePersonaDrafts();
                if (!personaDrafts.TryGetValue(id, out var draft))
                    throw new ArgumentException("人格草稿不存在或已经过期，请重新生成。");
                RequireLearningContact(draft.UserId);
                // Hold ownership through compare/save/removal, including a no-op confirmation whose hash stays unchanged.
                value = draft.ProposedPersona with
                {
                    Name = body.GetProperty("name").GetString()!,
                    Personality = body.GetProperty("personality").GetString()!,
                    UserAddress = body.GetProperty("userAddress").GetString()!,
                    SpeakingStyle = body.GetProperty("speakingStyle").GetString()!
                };
                value.Validate();
                ct.ThrowIfCancellationRequested();
                target = draft.UserId;
                saved = profiles.TrySaveContactPersona(
                    new ContactPersona(draft.UserId, value.Name, value.Personality, value.UserAddress, value.SpeakingStyle),
                    draft.BasePersonaHash);
                if (saved) personaDrafts.Remove(id);
            }
            if (!saved)
            { await JsonAsync(context.Response, new { error = "这位联系人的角色在生成草稿后已被修改；为保护新修改，本次未保存，请重新生成。" }, 409, ct); return; }
            MarkDirty();
            // 草稿保存的是这位联系人的专属人格；全局角色不受影响。
            await JsonAsync(context.Response, new { userId = target, persona = profiles.GetContactPersona(target),
                effective = profiles.EffectivePersona(target) }, 200, ct);
            return;
        }
        context.Response.StatusCode = 404;
    }

}
