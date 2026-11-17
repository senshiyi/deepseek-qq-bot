using AudioBridge;

/// <summary>用真实数据跑一次私聊生成：看"好感度"这一行是否真的进了提示，以及她的回复会不会因为数值不同而变化。</summary>
public static class AffectionProbe
{
    public static async Task<int> RunAsync(long userId, int affection, bool group = false)
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
        var original = companions.Get(userId);
        try
        {
            companions.SetMeters(userId, affection, original.Possession);
            var now = DateTimeOffset.UtcNow;
            var message = group
                ? new ParsedGroupMessage(1095601728, userId, 9001, now, "你好呀，今天有点累", false, false)
                : new ParsedGroupMessage(0, userId, 9001, now, "你好呀，今天有点累", false, false, IsPrivate: true);
            // 走运行时的同一条拼装路径：群聊只带态度、不带私人日记。
            var reference = GroupChatRuntime.BuildReferenceContext(message, [message], profiles, contacts, persona) +
                GroupChatRuntime.CompanionReference(message, companions);
            var marker = "【你此刻对他的感觉】";
            Console.WriteLine("mode=" + (group ? "group" : "private") + " affection=" + affection + " reference chars=" + reference.Length +
                " mentionsMeters=" + reference.Contains(marker, StringComparison.Ordinal));
            if (reference.Contains(marker, StringComparison.Ordinal))
            {
                var start = reference.IndexOf(marker, StringComparison.Ordinal);
                var end = reference.IndexOf('\n', start + marker.Length);
                end = end < 0 ? reference.Length : end;
                var second = reference.IndexOf('\n', end + 1);
                Console.WriteLine("feeling block: " + reference[start..(second < 0 ? reference.Length : second)].Replace("\n", " | "));
            }
            var seen = new List<string>();
            var brain = new GroupChatBrain(memes.List, (question, catalog, speakerPersona) =>
            {
                var prompt = GroupChatBrain.SystemPrompt(catalog.Select(m => (m.Id, m.Tags)), GroupChatMode.Mention, true, speakerPersona, false);
                return new SchoolModelReply(school, key, budget, prompt, maximumOutputTokens: 2048);
            }, 1667533356, persona, (_, _) => reference, null, null, null, null, null);
            var reply = await brain.GenerateAsync([message], CancellationToken.None, message, group ? GroupChatTrigger.Ambient : GroupChatTrigger.Private);
            Console.WriteLine("reply: " + reply.Text.Replace("\n", " "));
        }
        finally
        {
            companions.SetMeters(userId, original.Affection, original.Possession);
            var restored = companions.Get(userId);
            Console.WriteLine("restored=" + restored.Affection + "/" + restored.Possession);
        }
        return 0;
    }
}
