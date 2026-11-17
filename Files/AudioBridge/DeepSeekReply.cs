using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioBridge;

// This run uses only deepseek-flash. Reserve a conservative peak-price ceiling before every request.
public sealed class ApiBudget
{
    private readonly object gate = new();
    private readonly string ledgerPath;
    private readonly double limit;
    private readonly int maxCalls;
    private double reserved;
    private int calls;
    public ApiBudget(string ledgerPath, double limitYuan, int maxCalls)
    {
        this.ledgerPath = ledgerPath;
        limit = limitYuan;
        this.maxCalls = maxCalls;
        if (File.Exists(ledgerPath))
        {
            using var saved = JsonDocument.Parse(File.ReadAllText(ledgerPath));
            reserved = saved.RootElement.GetProperty("reservedYuan").GetDouble();
            calls = saved.RootElement.GetProperty("calls").GetInt32();
            if (!double.IsFinite(reserved) || reserved < 0 || calls < 0) throw new IOException("预算记录无效；停止 API 调用。");
        }
    }

    public object Snapshot() { lock (gate) return new { reservedYuan = reserved, limitYuan = limit, calls, maxCalls }; }

    public void Reserve(IReadOnlyList<ChatTurn> history, int imageCount = 0, int maxOutputTokens = 256)
    {
        if (imageCount is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(imageCount));
        if (maxOutputTokens is < 256 or > 8192) throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        // UTF-8 byte count bounds ordinary text token count; extra per-message and fixed overhead is deliberate.
        // Official vision docs cap each image at 1024 tokens; reserve 2048 for extra margin.
        var inputTokenCeiling = checked(history.Sum(t => (long)Encoding.UTF8.GetByteCount(t.Content) + 128) + 4096 + 2048L * imageCount);
        var costCeiling = (inputTokenCeiling * 2d + maxOutputTokens * 8d) / 1_000_000d;
        lock (gate)
        {
            // Voice and text runtimes share this ledger. Reload under a cross-process mutex,
            // so another runtime's reservation cannot be overwritten by a stale in-memory total.
            var mutexName = "Local\\DeepSeekQqBudget_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(ledgerPath).ToUpperInvariant())));
            using var mutex = new Mutex(false, mutexName);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(1)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("预算账本正被其他进程占用；本次不调用API。");
                if (File.Exists(ledgerPath))
                {
                    using var saved = JsonDocument.Parse(File.ReadAllText(ledgerPath));
                    reserved = saved.RootElement.GetProperty("reservedYuan").GetDouble();
                    calls = saved.RootElement.GetProperty("calls").GetInt32();
                    if (!double.IsFinite(reserved) || reserved < 0 || calls < 0) throw new IOException("预算记录无效；停止 API 调用。");
                }
            if (calls >= maxCalls || reserved + costCeiling > limit)
                throw ReplyFailure.Tag(new InvalidOperationException("达到本轮 DeepSeek 调用次数或累计预算上限。"), "deepseek_budget");
            var next = new { reservedYuan = reserved + costCeiling, calls = calls + 1, limitYuan = limit,
                accounting = "Conservative peak-price reservation; no refunds, not an account balance or bill.", at = DateTimeOffset.Now };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ledgerPath))!);
            var temporary = ledgerPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, BridgeConfig.JsonOptions));
            File.Move(temporary, ledgerPath, true);
            reserved = next.reservedYuan;
            calls = next.calls;
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
    }
}

public sealed class DeepSeekReply : IStreamingReply, IInlineImageSink, IDisposable
{
    private readonly HttpClient http;
    private readonly ApiBudget budget;
    private readonly string apiKey;
    private readonly ImageQuestionContext? images;
    private readonly string? systemPrompt;
    private readonly bool jsonMode;
    private readonly string? imageQuestion;
    private readonly int maximumOutputTokens;
    private readonly TimeSpan requestTimeout;
    private IReadOnlyList<InlineImage>? attachedImages;
    public string? ObservedModel { get; private set; }

    /// <summary>聊天入口在生成前附上本轮已授权的图片；语音入口仍由 ImageQuestionContext 自行解析。</summary>
    public void AttachImages(IReadOnlyList<InlineImage> images) => attachedImages = images ?? [];

    public DeepSeekReply(string apiKey, ApiBudget budget, HttpMessageHandler? handler = null, ImageQuestionContext? images = null,
        string? systemPrompt = null, bool jsonMode = false, string? imageQuestion = null, int maximumOutputTokens = 256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (maximumOutputTokens is < 256 or > 8192) throw new ArgumentOutOfRangeException(nameof(maximumOutputTokens));
        this.apiKey = apiKey;
        this.budget = budget;
        this.images = images;
        this.systemPrompt = systemPrompt;
        this.jsonMode = jsonMode;
        this.imageQuestion = imageQuestion;
        this.maximumOutputTokens = maximumOutputTokens;
        requestTimeout = TimeSpan.FromSeconds(maximumOutputTokens > 1024 ? 120 : 25);
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;
    }

    internal DeepSeekReply(string apiKey, ApiBudget budget, HttpMessageHandler handler, TimeSpan requestTimeout)
        : this(apiKey, budget, handler)
    {
        if (requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromSeconds(120))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        this.requestTimeout = requestTimeout;
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var source = StreamCoreAsync(history, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool next;
            try { next = await source.MoveNextAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { throw new OperationCanceledException("DeepSeek 请求已取消，既有预算预留保留。", ct); }
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException or JsonException or InvalidOperationException or DecoderFallbackException)
            {
                // 已经分类过的（HTTP 状态码）原样抛出；其余归为接口不可用，别把原因压成一句笼统的 io_error。
                if (ReplyFailure.HasCode(error)) throw;
                throw ReplyFailure.Tag(new IOException("DeepSeek 请求超时、失败或响应无效；既有预算预留保留，不自动重试。"), "deepseek_unavailable");
            }
            if (!next) yield break;
            yield return source.Current;
        }
    }

    private async IAsyncEnumerable<string> StreamCoreAsync(IReadOnlyList<ChatTurn> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var selected = attachedImages is not null ? new ImageQuestionContextResult(null, attachedImages) :
            images is null ? new ImageQuestionContextResult(null, []) :
            await images.ResolveAsync(imageQuestion ?? history.LastOrDefault(m => m.Role == "user")?.Content ?? "", ct).ConfigureAwait(false);
        if (selected.Clarification is { } clarification)
        {
            yield return jsonMode ? JsonSerializer.Serialize(new { send = true, text = clarification, memeId = (string?)null }) : clarification;
            yield break;
        }
        var messages = new List<ChatTurn>
        {
            new("system", systemPrompt ?? "你在QQ群语音中交流。只输出适合朗读的中文正文，回答简短自然，最多三句，不超过180字。不要输出Markdown、推理过程、代码或工具命令。音轨是多人混合，不能猜测说话人的QQ身份；多人重叠时请简短请求轮流重说。你不能操作电脑或读写文件。不主动说出自己的唤醒名字。图片不可见时明确说明，不编造图片内容。")
        };
        messages.AddRange(history);
        budget.Reserve(messages, selected.Images.Count, maximumOutputTokens);
        var lastUser = messages.FindLastIndex(m => m.Role == "user");
        var payload = messages.Select((m, index) =>
        {
            object content = m.Content;
            if (index == lastUser && selected.Images.Count > 0)
            {
                var blocks = new List<object> { new { type = "text", text = m.Content } };
                blocks.AddRange(selected.Images.Select(img => (object)new { type = "image_url", image_url = new { url = img.DataUrl, detail = "original" } }));
                content = blocks;
            }
            return new { role = m.Role, content };
        }).ToArray();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var requestBody = new Dictionary<string, object>
        {
            ["model"] = "deepseek-flash", ["stream"] = true, ["thinking"] = new { type = "disabled" },
            ["max_tokens"] = maximumOutputTokens, ["messages"] = payload
        };
        if (jsonMode) requestBody["response_format"] = new { type = "json_object" };
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var code = status switch
            {
                401 or 403 => "deepseek_auth",
                402 => "deepseek_balance",
                429 => "deepseek_rate_limit",
                400 or 413 or 422 => "deepseek_rejected",
                _ => "deepseek_unavailable"
            };
            throw ReplyFailure.Tag(new IOException($"DeepSeek 请求失败（HTTP {status}）；本轮停止，可检查密钥、余额或网络。"), code);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var seenBytes = 0;
        while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            seenBytes = checked(seenBytes + Encoding.UTF8.GetByteCount(line));
            if (seenBytes > 512 * 1024) throw new IOException("DeepSeek 流超过允许大小，已停止本轮。");
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line[5..].Trim();
            if (json == "[DONE]") yield break;
            if (json.Length == 0) continue;
            using var item = JsonDocument.Parse(json);
            if (item.RootElement.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                ObservedModel = model.GetString();
            if (!item.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            if (!choices[0].TryGetProperty("delta", out var delta)) continue;
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                yield return text;
        }
        throw new IOException("DeepSeek 流提前断开，未收到结束标记。");
    }
    public void Dispose() => http.Dispose();
}

public sealed class FixedReply(string text) : IStreamingReply
{
    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield return text;
    }
}
