using AudioBridge;

/// <summary>真实模型验证：私聊排队的那句在她已经回过一句之后，还会不会复读。</summary>
public static class RepeatGuardProbe
{
    public static async Task<int> RunAsync(long userId)
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
        var now = DateTimeOffset.UtcNow;
        // 复现线上那一轮：她回了"明白就去吃 别磨蹭"，然后才轮到排队的"我知道惹"。
        var queued = new ParsedGroupMessage(0, userId, 9101, now, "我知道惹", false, false, IsPrivate: true);
        var own = queued with { SenderId = 1667533356, MessageId = 0, Text = "明白就去吃 别磨蹭", At = now.AddSeconds(30) };
        var reference = GroupChatRuntime.BuildReferenceContext(queued, [queued], profiles, contacts, persona) +
            GroupChatRuntime.CompanionReference(queued, companions);
        var brain = new GroupChatBrain(memes.List, (question, catalog, speakerPersona) =>
        {
            var prompt = GroupChatBrain.SystemPrompt(catalog.Select(m => (m.Id, m.Tags)), GroupChatMode.Mention, true, speakerPersona, false);
            return new SchoolModelReply(school, key, budget, prompt, maximumOutputTokens: 2048);
        }, 1667533356, persona, (_, _) => reference, null, null, null, null, null);
        var reply = await brain.GenerateAsync([queued, own], CancellationToken.None, queued, GroupChatTrigger.Private);
        Console.WriteLine("send=" + reply.Send + " text=" + reply.Text.Replace("\n", " ") + " lines=" + (reply.Messages?.Length ?? 0));
        return 0;
    }
}
