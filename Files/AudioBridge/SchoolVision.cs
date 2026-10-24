using System.Text;

namespace AudioBridge;

/// <summary>
/// 用学校网关里的视觉模型把图片转述成一段中文，供不能直读图片的文字模型使用。
/// 只做一次请求、不重试；点数照常按学校账本预留。
/// </summary>
public sealed class SchoolVision
{
    public const int MaximumCaptionCharacters = 400;
    // 视觉模型的思考过程同样占用输出额度：768 曾把额度全耗在推理上，导致正文为空、整次转述失败。
    public const int CaptionOutputTokens = 2048;
    private const int MaximumQuestionCharacters = 200;
    private const string Instruction =
        "你是视觉转述器。用中文客观描述这张图片：里面有什么、关键细节、以及图中能读到的文字；看不清或不确定就直接说明。只输出描述本身，不要评价、不要扮演、不要输出Markdown，不超过200字。对方的问题（仅作参考，不要照抄）：";

    private readonly SchoolModelSettings settings;
    private readonly string key;
    private readonly SchoolUsageBudget budget;
    private readonly HttpMessageHandler? handler;

    public SchoolVision(SchoolModelSettings settings, string key, SchoolUsageBudget budget, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(budget);
        this.settings = settings; this.key = key; this.budget = budget; this.handler = handler;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(settings.VisionModel);

    public async Task<string> DescribeAsync(string question, IReadOnlyList<InlineImage> images, CancellationToken ct)
    {
        if (!IsEnabled) throw new InvalidOperationException("学校视觉模型未配置，本轮不读图。");
        if (images is null || images.Count is < 1 or > 2) throw new ArgumentException("一次只能转述一至两张图片。", nameof(images));
        ct.ThrowIfCancellationRequested();
        var vision = settings with { Model = settings.VisionModel! };
        using var reply = new SchoolModelReply(vision, key, budget,
            Instruction + Clip(question, MaximumQuestionCharacters), handler, maximumOutputTokens: CaptionOutputTokens,
            images: images, allowTruncated: true);
        var text = new StringBuilder();
        await foreach (var chunk in reply.StreamAsync([new("user", "请转述这张图片。")], ct).WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (chunk is null) continue;
            // 视觉模型的思考过程会占用输出额度，正文本身不该无界。
            if (chunk.Length > MaximumCaptionCharacters * 8 - text.Length)
                throw new InvalidDataException("图片转述过长，已丢弃。");
            text.Append(chunk);
        }
        ct.ThrowIfCancellationRequested();
        var caption = Collapse(text.ToString());
        if (caption.Length == 0) throw new InvalidDataException("视觉模型没有返回可用的图片描述，本轮不猜测图片内容。");
        return Clip(caption, MaximumCaptionCharacters);
    }

    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
            builder.Append(char.IsControl(character) ? ' ' : character);
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Clip(string value, int maximum)
    {
        var text = value.Trim();
        if (text.Length <= maximum) return text;
        var end = maximum;
        if (char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end].Trim();
    }
}
