using AudioBridge;

/// <summary>真实验证"自己联网找表情包"：真实搜索、真实下载校验、真实模型是否会用 findMeme。</summary>
public static class FindMemeProbe
{
    public static async Task<int> RunAsync()
    {
        var project = Environment.CurrentDirectory;
        const string keyword = "可爱猫咪表情包";
        var temp = Path.Combine(Path.GetTempPath(), "meme-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using var web = new PublicWeb();
            var school = SchoolModelSettings.Load(Path.Combine(project, "school-api.local.json"));
            var key = BotSettings.LoadSecret(project, "SCHOOL_API_KEY") ?? throw new InvalidOperationException("缺少 SCHOOL_API_KEY。");
            var budget = new SchoolUsageBudget(Path.Combine(project, "Results", "local", "school-api-budget.json"), school);
            var vision = new SchoolVision(school, key, budget);
            string? caption = null;
            var found = await web.SearchImagesAsync(keyword, CancellationToken.None);
            Console.WriteLine("search '" + keyword + "' candidates=" + found.Count);
            foreach (var item in found.Take(5)) Console.WriteLine("  " + item.Title + " | " + item.Url);
            var saved = 0;
            var library = new MemeLibrary(temp, "Memes");
            foreach (var item in found)
            {
                if (saved >= 2) break;
                try
                {
                    var bytes = await web.DownloadImageAsync(item.Url, CancellationToken.None);
                    var id = "self-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12].ToLowerInvariant();
                    var entry = library.ImportBytes(id, bytes, [keyword]);
                    Console.WriteLine("  saved " + entry.Id + " bytes=" + bytes.Length);
                    if (caption is null)
                    {
                        var mime = MemeImageFormat.Validate(bytes);
                        caption = await vision.DescribeAsync("", [new InlineImage(mime, "data:" + mime + ";base64," + Convert.ToBase64String(bytes))], CancellationToken.None);
                        Console.WriteLine("  caption: " + caption);
                    }
                    saved++;
                }
                catch (Exception error) { Console.WriteLine("  skip " + error.GetType().Name); }
            }
            var memes = new MemeLibrary(project, "Files/Memes");
            var persona = new PersonaSettings { Name = "大肥鱼", UserAddress = "主人", ReplyMaxCharacters = 3000 };
            var message = new ParsedGroupMessage(0, 29070481, 5150, DateTimeOffset.Now, "帮我找张可爱的猫咪表情包存起来", false, false, IsPrivate: true);
            var searched = new List<string>();
            var brain = new GroupChatBrain(memes.List, (question, catalog, _) => new SchoolModelReply(school, key, budget,
                GroupChatBrain.SystemPrompt(catalog.Select(m => (m.Id, (IReadOnlyList<string>)m.Tags)), GroupChatMode.Mention, privateChat: true, persona, voiceEnabled: true),
                maximumOutputTokens: 2048),
                1667533356, persona, null, null, null, null,
                (word, _) =>
                {
                    searched.Add(word);
                    return Task.FromResult(new MemeFindResult(
                        "已经联网找到并收藏了一张与「" + word + "」有关的表情包，下一轮可以自己发它。", "self-probe", caption));
                });
            var reply = await brain.GenerateAsync([message], CancellationToken.None, message, GroupChatTrigger.Private);
            Console.WriteLine("model text=" + reply.Text.Replace("\n", " "));
            Console.WriteLine("model requested search=" + (searched.Count == 1 ? "yes '" + searched[0] + "'" : "no/multiple(" + searched.Count + ")"));
            Console.WriteLine("model final findMeme=" + (reply.FindMeme ?? "null"));
            Console.WriteLine("model memeTags=" + (reply.MemeTags is null ? "null" : string.Join("/", reply.MemeTags)));
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
        return 0;
    }
}
