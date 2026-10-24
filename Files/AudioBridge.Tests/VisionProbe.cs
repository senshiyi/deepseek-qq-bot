using System.Text.Json;
using AudioBridge;

/// <summary>真实调用学校网关的视觉模型一次，验证本轮识图接线；只写结果，不打印密钥或完整响应。</summary>
public static class VisionProbe
{
    public static async Task<int> RunAsync(string? imagePathOverride = null)
    {
        var project = Environment.CurrentDirectory;
        var settings = SchoolModelSettings.Load(Path.Combine(project, "school-api.local.json"));
        var key = BotSettings.LoadSecret(project, "SCHOOL_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("缺少 SCHOOL_API_KEY；未调用模型。");
        var ledger = Path.Combine(project, "Results", "local", "school-api-budget.json");
        var budget = new SchoolUsageBudget(ledger, settings);
        var before = JsonSerializer.SerializeToElement(budget.Snapshot());
        var imagePath = imagePathOverride is null
            ? Path.Combine(project, "Cache", "QwenVisionProbe", "synthetic-shapes.png")
            : Path.GetFullPath(imagePathOverride);
        if (!File.Exists(imagePath)) throw new InvalidOperationException("缺少合成图片；未调用模型。");
        var bytes = await File.ReadAllBytesAsync(imagePath);
        var mime = bytes.Length > 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[8] == 0x57 && bytes[9] == 0x45 ? "image/webp"
            : bytes.Length > 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 ? "image/gif"
            : bytes.Length > 4 && bytes[0] == 0xff && bytes[1] == 0xd8 ? "image/jpeg" : "image/png";
        var images = new[] { new InlineImage(mime, "data:" + mime + ";base64," + Convert.ToBase64String(bytes)) };
        var captioner = new SchoolVision(settings, key, budget);
        var record = new Dictionary<string, object?>
        {
            ["version"] = 1, ["at"] = DateTimeOffset.Now, ["source"] = "school",
            ["probeMode"] = imagePathOverride is null ? "synthetic_image_via_SchoolVision" : "chosen_image_via_SchoolVision",
            ["realQqMessagesSent"] = 0, ["realUserImagesUploaded"] = 0, ["automaticRetry"] = false,
            ["textModel"] = settings.Model, ["visionModel"] = settings.VisionModel,
            ["syntheticFixture"] = new { relativePath = Path.GetRelativePath(project, imagePath), mimeType = mime,
                sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), bytes = bytes.Length },
            ["budgetBefore"] = before
        };
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var caption = await captioner.DescribeAsync("图里左边和右边各是什么形状、什么颜色？", images,
                new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token);
            record["status"] = "ok";
            record["caption"] = caption;
            record["captionMatchesFixture"] = caption.Contains("红", StringComparison.Ordinal) && caption.Contains("蓝", StringComparison.Ordinal);
            Console.WriteLine("caption: " + caption);
        }
        catch (Exception error)
        {
            record["status"] = "failed";
            record["failureType"] = error.GetType().Name;
            record["failure"] = error.Message;
            Console.WriteLine("vision probe failed: " + error.GetType().Name);
        }
        record["elapsedMilliseconds"] = Math.Round(started.Elapsed.TotalMilliseconds);
        record["budgetAfter"] = JsonSerializer.SerializeToElement(new SchoolUsageBudget(ledger, settings).Snapshot());
        record["limitations"] = "一次合成图片只证明该网关与接线可用；QQ 图片元数据、私聊入口、动图、OCR 质量和真实回复均未由本探测覆盖。";
        Directory.CreateDirectory(Path.Combine(project, "Results", "local"));
        var output = Path.Combine(project, "Results", "local", "vision-probe-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + ".json");
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine("wrote " + Path.GetFileName(output) + "; status=" + record["status"]);
        return record["status"] is "ok" ? 0 : 1;
    }
}
