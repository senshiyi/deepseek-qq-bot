using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioBridge;

public sealed class SchoolUsageBudget
{
    public const int MaximumOutputTokens = 256;
    private readonly string path, mutexName;
    private readonly SchoolModelSettings settings;
    private decimal reserved;
    private int calls;
    public SchoolUsageBudget(string ledgerPath, SchoolModelSettings settings)
    {
        settings.Validate(); this.settings = settings; path = Path.GetFullPath(ledgerPath);
        mutexName = "Global\\SchoolQqBudget_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        Locked(() => 0);
    }

    public object Snapshot() => Locked<object>(() => new { reservedPoints = reserved, maxPoints = settings.MaxPoints, calls, maxCalls = settings.MaxCalls });

    public void Reserve(IReadOnlyList<ChatTurn> history, int maxOutputTokens = 256, int imageCount = 0)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (maxOutputTokens is < 256 or > 8192) throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        if (imageCount is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(imageCount));
        // 实测 1170×2532 截图占 2998 输入 token；单张图片按 4096 token 的上界预留，宁可多算不欠账。
        var ceiling = checked(history.Sum(turn => (long)Encoding.UTF8.GetByteCount(turn.Content) + 128) + 4096 + 4096L * imageCount);
        var cost = checked((ceiling * settings.InputPointsPerMillion!.Value + maxOutputTokens * settings.OutputPointsPerMillion!.Value) / 1_000_000m);
        Locked(() =>
        {
            var nextReserved = checked(reserved + cost);
            var nextCalls = checked(calls + 1);
            if (settings.MaxCalls is { } maxCalls && calls >= maxCalls ||
                settings.MaxPoints is { } maxPoints && nextReserved > maxPoints)
                throw ReplyFailure.Tag(new InvalidOperationException("学校模型累计点数或调用次数已达配置上限；本次未调用，不重置既有累计。"), "school_budget");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new { version = 1, reservedPoints = nextReserved,
                    calls = nextCalls, model = settings.Model, accounting = "Conservative points reservation; no refund; not official balance", at = DateTimeOffset.Now }));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            reserved = nextReserved; calls = nextCalls; return 0;
        });
    }

    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, mutexName);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(1)); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new IOException("学校点数账本正被占用；本次不调用模型。");
            if (File.Exists(path))
            {
                try
                {
                    using var saved = JsonDocument.Parse(File.ReadAllText(path));
                    var root = saved.RootElement;
                    if (root.GetProperty("version").GetInt32() != 1) throw new JsonException();
                    var nextReserved = root.GetProperty("reservedPoints").GetDecimal();
                    var nextCalls = root.GetProperty("calls").GetInt32();
                    if (nextReserved < 0 || nextCalls < 0) throw new JsonException();
                    reserved = nextReserved; calls = nextCalls;
                }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
                { throw new IOException("学校点数账本无效；停止调用，不重置累计。"); }
            }
            else if (calls != 0 || reserved != 0) throw new IOException("学校点数账本丢失；停止调用，不重置累计。");
            return action();
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }
}

/// <summary>Chat Completions。文字模型只能读文字；传入图片时用于视觉模型转述，仍会完整校验后才返回正文。</summary>
public sealed class SchoolModelReply : IStreamingReply, IDisposable
{
    public const int MaximumResponseBytes = 512 * 1024;
    private const string TextOnly = "\n本轮没有可读的图片；遇到图片问题请简短请对方描述图片或重发，不得编造所见。";
    private const string ImageTurn = "\n本轮带有图片，直接据此回答；不要声称看过原图之外的内容，也不要编造图中没有的细节。";
    private readonly SchoolModelSettings settings;
    private readonly SchoolUsageBudget budget;
    private readonly HttpClient http;
    private readonly string systemPrompt;
    private readonly TimeSpan timeout;
    private readonly int maximumOutputTokens;
    public string? ObservedModel { get; private set; }

    private readonly IReadOnlyList<InlineImage> images;
    private readonly bool allowTruncated;

    public SchoolModelReply(SchoolModelSettings settings, string key, SchoolUsageBudget budget,
        string systemPrompt, HttpMessageHandler? handler = null, int maximumOutputTokens = 256,
        IReadOnlyList<InlineImage>? images = null, bool allowTruncated = false)
        : this(settings, key, budget, systemPrompt, handler, TimeSpan.FromSeconds(maximumOutputTokens > 1024 ? 120 : 25),
            maximumOutputTokens, images, allowTruncated) { }

    internal SchoolModelReply(SchoolModelSettings settings, string key, SchoolUsageBudget budget,
        string systemPrompt, HttpMessageHandler? handler, TimeSpan timeout, int maximumOutputTokens = 256,
        IReadOnlyList<InlineImage>? images = null, bool allowTruncated = false)
    {
        settings.Validate();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && !"-._~+/=".Contains(c)))
            throw new ArgumentException("请配置有效的学校API密钥。");
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentNullException.ThrowIfNull(budget);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maximumOutputTokens is < 256 or > 8192) throw new ArgumentOutOfRangeException(nameof(maximumOutputTokens));
        if (images is { Count: > 2 }) throw new ArgumentException("一轮最多附带两张图片。", nameof(images));
        this.images = images ?? [];
        this.allowTruncated = allowTruncated;
        this.settings = settings; this.budget = budget;
        this.systemPrompt = systemPrompt + (this.images.Count == 0 ? TextOnly : ImageTurn);
        this.timeout = timeout;
        this.maximumOutputTokens = maximumOutputTokens;
        handler ??= CreateHandler(settings);
        if (handler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; }
        if (handler is HttpClientHandler client) { client.AllowAutoRedirect = false; client.UseCookies = false; }
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    /// <summary>
    /// 校内模型入口一律直连：Clash 这类系统代理会把 10.x 校内网关当成外网转给节点，TLS 握手直接失败。
    /// 只有调用方显式传 handler 时（测试）才沿用它的代理设置。
    /// </summary>
    internal static SocketsHttpHandler CreateHandler(SchoolModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5), MaxResponseHeadersLength = 32
        };
        if (string.IsNullOrWhiteSpace(settings.ResolveIp)) return handler;
        // 本地 DNS 把网关的内网 A 记录过滤掉时（smartdns/Clash 很常见），用配置的 IP 直连；SNI 和证书仍按域名走。
        var address = IPAddress.Parse(settings.ResolveIp.Trim());
        handler.ConnectCallback = async (context, token) =>
        {
            var socket = new System.Net.Sockets.Socket(address.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        };
        return handler;
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history, [EnumeratorCancellation] CancellationToken ct)
    {
        var text = await CompleteAsync(history, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        yield return text;
    }

    private async Task<string> CompleteAsync(IReadOnlyList<ChatTurn> history, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(history);
        if (history.Any(turn => turn is null || turn.Content is null || turn.Role is not ("system" or "user" or "assistant")))
            throw new ArgumentException("学校文本入口只允许system/user/assistant文字消息。");
        var messages = new[] { new ChatTurn("system", systemPrompt) }.Concat(history).ToArray();
        budget.Reserve(messages, maximumOutputTokens, images.Count);
        var lastUser = Array.FindLastIndex(messages, turn => turn.Role == "user");
        var payload = messages.Select((turn, index) =>
        {
            object content = turn.Content;
            if (images.Count > 0 && index == lastUser)
                content = new List<object> { (object)new { type = "text", text = turn.Content } }
                    .Concat(images.Select(image => (object)new { type = "image_url",
                        image_url = new { url = image.DataUrl, detail = "original" } })).ToArray();
            return new { role = turn.Role, content };
        }).ToArray();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.ChatEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model = settings.Model, stream = settings.Stream,
                    max_tokens = maximumOutputTokens, messages = payload }), Encoding.UTF8, "application/json")
            };
            var send = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            HttpResponseMessage response;
            try { response = await send.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch
            {
                _ = send.ContinueWith(done => { if (done.IsCompletedSuccessfully) done.Result.Dispose(); else _ = done.Exception; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            using (response)
            {
                if (!response.IsSuccessStatusCode || response.Headers.Location is not null ||
                    response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri ||
                    response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength is > MaximumResponseBytes ||
                    response.Content.Headers.ContentType?.MediaType != (settings.Stream ? "text/event-stream" : "application/json")) throw Invalid();
                await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var read = input.ReadAsync(buffer, deadline.Token).AsTask();
                    _ = read.ContinueWith(faulted => _ = faulted.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    var count = await read.WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (output.Length + count > MaximumResponseBytes) throw Invalid();
                    output.Write(buffer, 0, count);
                }
                deadline.Token.ThrowIfCancellationRequested();
                var body = new UTF8Encoding(false, true).GetString(output.ToArray());
                var result = settings.Stream ? ParseEvents(body, deadline.Token) : ParseComplete(body);
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException("学校模型请求已取消，既有点数预留保留；不自动重试。", ct); }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or JsonException or InvalidOperationException or DecoderFallbackException or KeyNotFoundException)
        {
            // 预算上限这类已经分类过的错误保留原码，别盖成"接口连不上"。
            if (ReplyFailure.HasCode(error)) throw;
            throw ReplyFailure.Tag(new IOException("学校模型请求超时、失败或响应不完整；既有点数预留保留，不自动重试或切换服务。"), "school_unavailable");
        }
    }

    private string ParseComplete(string body)
    {
        using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 24 });
        var root = doc.RootElement;
        RejectDuplicateFields(root);
        ObserveModel(root);
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1) throw Invalid();
        var choice = choices[0];
        if (!choice.TryGetProperty("finish_reason", out var finish) || finish.ValueKind != JsonValueKind.String ||
            finish.GetString() != "stop" && !(allowTruncated && finish.GetString() == "length") ||
            !choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object || HasTools(message) ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString())) throw Invalid();
        return content.GetString()!;
    }

    private string ParseEvents(string body, CancellationToken ct)
    {
        using var reader = new StringReader(body);
        var text = new StringBuilder(); var done = false; var stopped = false;
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim(); if (data.Length == 0) continue;
            if (done) throw Invalid();
            if (data == "[DONE]") { done = true; continue; }
            using var doc = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 24 });
            var root = doc.RootElement; RejectDuplicateFields(root); ObserveModel(root);
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() > 1) throw Invalid();
            if (choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object || HasTools(delta)) throw Invalid();
            if (delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
            {
                if (content.ValueKind != JsonValueKind.String || stopped) throw Invalid();
                text.Append(content.GetString());
            }
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)
            {
                if (stopped || finish.ValueKind != JsonValueKind.String ||
                    finish.GetString() != "stop" && !(allowTruncated && finish.GetString() == "length")) throw Invalid();
                stopped = true;
            }
        }
        if (!done || !stopped || text.Length == 0) throw Invalid();
        return text.ToString();
    }

    private void ObserveModel(JsonElement root)
    { if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String) ObservedModel = model.GetString(); }
    private static void RejectDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw Invalid(); RejectDuplicateFields(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateFields(item);
    }
    private static bool HasTools(JsonElement value) =>
        value.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null ||
        value.TryGetProperty("function_call", out var function) && function.ValueKind != JsonValueKind.Null;
    private static InvalidDataException Invalid() => new("学校模型响应无效。");
    public void Dispose() => http.Dispose();
}
