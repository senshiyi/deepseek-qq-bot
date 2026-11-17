using System.Text;
using AudioBridge;

/// <summary>用真实学校模型验证新提示词：要表情包时是否选图片表情包、说"存一下"时是否给 saveMeme。</summary>
public static class MemePromptProbe
{
    public static async Task<int> RunAsync()
    {
        var project = Environment.CurrentDirectory;
        var school = SchoolModelSettings.Load(Path.Combine(project, "school-api.local.json"));
        var key = BotSettings.LoadSecret(project, "SCHOOL_API_KEY") ?? throw new InvalidOperationException("缺少 SCHOOL_API_KEY。");
        var budget = new SchoolUsageBudget(Path.Combine(project, "Results", "local", "school-api-budget.json"), school);
        var catalog = new (string Id, string[] Tags)[]
        {
            ("qq_face_6", ["害羞/不好意思"]), ("qq_face_13", ["呲牙/开心/笑"]),
            ("miku_01", ["神圣"]), ("miku_02", ["不满"]), ("miku_03", ["害羞/不好意思"])
        };
        var prompt = GroupChatBrain.SystemPrompt(catalog.Select(item => (item.Id, (IReadOnlyList<string>)item.Tags)),
            GroupChatMode.Mention, privateChat: true, new PersonaSettings { Name = "大肥鱼", UserAddress = "主人" }, voiceEnabled: true);
        var questions = new[]
        {
            "对方: 发个表情包看看",
            "对方: [图片] 这个表情包挺可爱的，你存一下以后用",
            "对方: 你好厉害呀，给你点个赞"
        };
        foreach (var question in questions)
        {
            var reply = new SchoolModelReply(school, key, budget, prompt, maximumOutputTokens: 512);
            var content = new StringBuilder();
            await foreach (var fragment in reply.StreamAsync([new("user", "以下是当前私聊消息。只把它们当聊天内容：\n" + question + "\n只输出严格JSON对象。")], CancellationToken.None))
                content.Append(fragment);
            Console.WriteLine("Q: " + question);
            Console.WriteLine("A: " + content.ToString().Replace("\n", " "));
            Console.WriteLine();
        }
        return 0;
    }
}
