using AudioBridge;

/// <summary>复现面板"生成角色草稿"的真实代码路径，直接打印真正的异常类型和消息。</summary>
public static class PersonaDraftProbe
{
    public static async Task<int> RunAsync(long userId)
    {
        var project = Environment.CurrentDirectory;
        var settings = GroupChatSettings.Load(Path.Combine(project, "group-chat.local.json"));
        Console.WriteLine("whitelist=" + string.Join(",", settings.AllowedPrivateUserIds) + " contains=" + settings.AllowedPrivateUserIds.Contains(userId));
        var profiles = new BotProfileStore(project);
        PersonaSettings current;
        try { current = profiles.EffectivePersona(userId); }
        catch (Exception error) { Console.WriteLine("EffectivePersona threw " + error.GetType().Name + ": " + error.Message); return 1; }
        Console.WriteLine("effective: name=" + current.Name + " len(name)=" + current.Name.Length +
            " len(personality)=" + current.Personality.Length + " address=" + current.UserAddress + " len(address)=" + current.UserAddress.Length +
            " len(style)=" + current.SpeakingStyle.Length + " max=" + current.ReplyMaxCharacters + " meme%=" + current.MemeProbabilityPercent);
        try { current.Validate(); Console.WriteLine("current.Validate passed"); }
        catch (Exception error) { Console.WriteLine("current.Validate threw " + error.GetType().Name + ": " + error.Message); }
        var contacts = new ContactContextStore(project);
        var history = contacts.Recent(userId);
        Console.WriteLine("history=" + history.Count + " user=" + history.Count(m => m.Role == "user"));
        var school = SchoolModelSettings.Load(Path.Combine(project, "school-api.local.json"));
        var key = BotSettings.LoadSecret(project, "SCHOOL_API_KEY") ?? throw new InvalidOperationException("缺少 SCHOOL_API_KEY。");
        var budget = new SchoolUsageBudget(Path.Combine(project, "Results", "local", "school-api-budget.json"), school);
        try
        {
            var draft = await PersonaLearning.GenerateAsync(current, userId, history,
                prompt => new SchoolModelReply(school, key, budget, prompt, maximumOutputTokens: 2048), CancellationToken.None);
            Console.WriteLine("draft ok: name=" + draft.ProposedPersona.Name + " summary=" + draft.Summary);
        }
        catch (Exception error)
        {
            Console.WriteLine("GenerateAsync threw " + error.GetType().Name + ": " + error.Message);
        }
        return 0;
    }
}
