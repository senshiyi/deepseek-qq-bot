using AudioBridge;

/// <summary>复现"先发图、再说存一下这个"的私聊场景，用真实学校模型看模型到底给不给 saveMeme。</summary>
public static class SaveSceneProbe
{
    public static async Task<int> RunAsync()
    {
        var project = Environment.CurrentDirectory;
        var school = SchoolModelSettings.Load(Path.Combine(project, "school-api.local.json"));
        var key = BotSettings.LoadSecret(project, "SCHOOL_API_KEY") ?? throw new InvalidOperationException("缺少 SCHOOL_API_KEY。");
        var budget = new SchoolUsageBudget(Path.Combine(project, "Results", "local", "school-api-budget.json"), school);
        var memes = new MemeLibrary(project, "Files/Memes");
        var persona = new PersonaSettings { Name = "大肥鱼", UserAddress = "主人", ReplyMaxCharacters = 3000 };
        var at = DateTimeOffset.Now.AddMinutes(-6);
        ParsedGroupMessage Msg(int offset, string text, bool self = false, bool image = false) =>
            new(0, self ? 1667533356 : 29070481, 900 + offset, at.AddMinutes(offset), text, false, image, IsPrivate: true);
        ParsedGroupMessage[] story =
        [
            Msg(0, "[图片]", image: true),
            Msg(1, "把这个记下"),
            Msg(2, "记下啦，是个蓝绿双马尾的小家伙，困困地抱着枕头趴着~", self: true),
        ];
        string[] asks = ["存一下这个", "把这个记下", "收藏一下刚才那张图"];
        foreach (var ask in asks)
        {
            var context = story.Append(Msg(3, ask)).ToArray();
            var trigger = context[^1];
            for (var sample = 1; sample <= 2; sample++)
            {
                var saved = new List<SaveMemeRequest>();
                var brain = new GroupChatBrain(memes.List, (question, catalog, _) =>
                    new SchoolModelReply(school, key, budget, GroupChatBrain.SystemPrompt(
                        catalog.Select(m => (m.Id, (IReadOnlyList<string>)m.Tags)), GroupChatMode.Mention, privateChat: true, persona, voiceEnabled: true),
                        maximumOutputTokens: 2048),
                    1667533356, persona, null, null, null,
                    (message, request, token) => { saved.Add(request); return Task.FromResult<string?>("self-probe"); });
                var reply = await brain.GenerateAsync(context, CancellationToken.None, trigger, GroupChatTrigger.Private);
                Console.WriteLine("ask=" + ask + " sample=" + sample + " saveMeme=" + (reply.SaveMeme is null ? "null" : "yes")
                    + " savedCalls=" + saved.Count + " memeId=" + (reply.MemeId ?? "null") + " send=" + reply.Send);
                Console.WriteLine("  text=" + reply.Text.Replace("\n", " "));
            }
        }
        return 0;
    }
}
