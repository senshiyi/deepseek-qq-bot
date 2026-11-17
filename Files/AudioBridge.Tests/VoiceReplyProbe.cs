using System.Text;
using AudioBridge;

/// <summary>真实模型验证：对方明确要求"发语音/唱"时，模型到底给不给 voice:true。</summary>
public static class VoiceReplyProbe
{
    public static async Task<int> RunAsync(long userId, string? triggerFile = null)
    {
        var project = Environment.CurrentDirectory;
        var profiles = new BotProfileStore(project);
        var contacts = new ContactContextStore(project);
        var companions = new CompanionStore(project);
        var school = SchoolModelSettings.Load(Path.Combine(project, "school-api.local.json"));
        var key = BotSettings.LoadSecret(project, "SCHOOL_API_KEY") ?? throw new InvalidOperationException("缺少 SCHOOL_API_KEY。");
        var budget = new SchoolUsageBudget(Path.Combine(project, "Results", "local", "school-api-budget.json"), school);
        var memes = new MemeLibrary(project, "Files/Memes");
        var persona = profiles.EffectivePersona(userId);
        var trigger = triggerFile is null ? "你去发个语音试试" : File.ReadAllText(triggerFile, Encoding.UTF8).Trim();
        var now = DateTimeOffset.UtcNow;
        var message = new ParsedGroupMessage(0, userId, 9201, now, trigger, false, false, IsPrivate: true);
        // 复现线上那一轮：她先说"嗓子还没装好、只能先发文字"，用户接着说"已经装好了/你去发个语音试试"。
        var excuse = message with
        {
            SenderId = 1667533356, MessageId = 0, At = now.AddSeconds(-30),
            Text = "嗓子还没装好呢 只能先给你个文字版 窗外的雨滴 一滴滴累积 屋内的湿气 像储存爱你的记忆"
        };
        var reference = GroupChatRuntime.BuildReferenceContext(message, [excuse, message], profiles, contacts, persona) +
            GroupChatRuntime.CompanionReference(message, companions);
        var brain = new GroupChatBrain(memes.List, (_, catalog, speakerPersona) =>
        {
            // 线上真实配置：voiceEnabled=true、voiceEmotion=true（ttsProvider=breeze）。
            var prompt = GroupChatBrain.SystemPrompt(catalog.Select(m => (m.Id, m.Tags)), GroupChatMode.Mention, true, speakerPersona, true, true);
            return new SchoolModelReply(school, key, budget, prompt, maximumOutputTokens: 2048);
        }, 1667533356, persona, (_, _) => reference, null, null, null, null, null, voiceEnabled: true);
        GroupChatReply reply;
        try { reply = await brain.GenerateAsync([excuse, message], CancellationToken.None, message, GroupChatTrigger.Private); }
        catch (Exception error) when (error is InvalidDataException)
        {
            Console.WriteLine("trigger=" + trigger);
            Console.WriteLine("reply failed: type=" + error.GetType().Name + " code=" + ReplyFailure.From(error).Code +
                " unknownField=" + (error.Data[GroupChatBrain.UnknownFieldKey] as string ?? "-"));
            return 3;
        }
        Console.WriteLine("trigger=" + trigger);
        Console.WriteLine("send=" + reply.Send + " voice=" + reply.Voice + " emotion=" + (reply.Emotion ?? "-") +
            " lines=" + (reply.Messages?.Length ?? 0) + " chars=" + reply.Text.Length);
        Console.WriteLine("text=" + reply.Text.Replace("\n", " / "));
        return reply.Voice ? 0 : 2;
    }
}
