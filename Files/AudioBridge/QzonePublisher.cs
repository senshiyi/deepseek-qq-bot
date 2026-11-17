using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AudioBridge;

/// <summary>
/// 每天最多自己发一条 QQ 空间说说：窗口 09:00–22:59，时刻由当天日期算出（同一天重启也不变），
/// 一天只尝试一次（模型选择不发也算一次），失败不重试。
/// </summary>
public static class QzoneSchedule
{
    public const int FromHour = 9;
    public const int ToHour = 23;

    /// <summary>当天的目标时刻：用日期做稳定散列，落在 09:00～22:59。</summary>
    public static DateTimeOffset Target(DateTimeOffset now)
    {
        var local = CompanionStore.ShanghaiTime(now);
        var date = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        var minutes = (ToHour - FromHour) * 60;
        var offset = (int)((ulong)DateOnly.FromDateTime(date).DayNumber * 2654435761UL % (ulong)minutes);
        return new DateTimeOffset(date.AddHours(FromHour).AddMinutes(offset), local.Offset);
    }

    public static bool ShouldPublish(bool enabled, DateTimeOffset? lastAttemptAt, DateTimeOffset now)
    {
        if (!enabled) return false;
        var local = CompanionStore.ShanghaiTime(now);
        if (local.Hour < FromHour || local.Hour >= ToHour) return false;
        if (lastAttemptAt is { } last && CompanionStore.ShanghaiTime(last).Date == local.Date) return false;
        return local >= Target(now);
    }
}

/// <summary>空间上一次尝试的时间，只用来保证一天一次；不保存说说内容。</summary>
public sealed class QzoneStore
{
    private const int MaximumBytes = 64 * 1024;
    private const int MaximumHandledComments = 200;
    private readonly string path;
    private readonly Func<DateTimeOffset> clock;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public QzoneStore(string projectDirectory, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var root = Path.GetFullPath(projectDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("空间状态的项目目录不存在。");
        path = Path.Combine(root, "Results", "local", "qzone-state.json");
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public QzoneState Load()
    {
        if (!File.Exists(path)) return new(null, [], false);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("空间状态文件为空或过大，未重置文件。");
        try
        {
            var document = JsonSerializer.Deserialize<Document>(bytes, Json) ?? throw new JsonException();
            if (document.Version != 1) throw new InvalidDataException("空间状态内容无效，未重置文件。");
            if (document.LastAttemptAt is { } at && at > clock().AddMinutes(5))
                throw new InvalidDataException("空间状态时间无效，未重置文件。");
            var handled = (document.HandledComments ?? []).Where(id => id.Length is > 0 and <= 80).TakeLast(MaximumHandledComments).ToArray();
            return new(document.LastAttemptAt, handled, document.CommentsSeeded);
        }
        catch (JsonException error) { throw new InvalidDataException("空间状态文件格式无效，未重置文件。", error); }
    }

    public void MarkAttempt(DateTimeOffset at) => Save(Load() with { LastAttemptAt = at });

    /// <summary>第一次读评论时登记历史评论，之后只回应新出现的。</summary>
    public void MarkSeeded() => Save(Load() with { Seeded = true });

    /// <summary>记下已经处理过的评论，避免同一句评论反复回应；只保留最近的若干条。</summary>
    public void MarkHandled(IEnumerable<string> ids)
    {
        var current = Load();
        var merged = current.HandledComments.Concat(ids.Where(id => id.Length is > 0 and <= 80))
            .Distinct(StringComparer.Ordinal).TakeLast(MaximumHandledComments).ToArray();
        Save(current with { HandledComments = merged });
    }

    private void Save(QzoneState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new Document(1, state.LastAttemptAt, state.HandledComments.ToList(), state.Seeded), Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("空间状态超过允许大小。");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record Document(int Version, DateTimeOffset? LastAttemptAt, List<string>? HandledComments, bool CommentsSeeded);
}

/// <summary>空间状态：上一次尝试发布的时间、已经处理过的评论标识，以及历史评论是否已登记。</summary>
public sealed record QzoneState(DateTimeOffset? LastAttemptAt, IReadOnlyList<string> HandledComments, bool Seeded);

/// <summary>
/// 用本机已登录的 QQ 会话把一句话发成空间说说。这是空间网页协议，属于非官方通道：
/// 只发一次、失败不重试，异常收敛成可读的失败原因，cookie 不写日志。
/// </summary>
public sealed class QzonePublisher : IDisposable
{
    public const int MaximumCharacters = 300;
    private const int MaximumResponseBytes = 64 * 1024;
    private const string PublishUrl = "https://user.qzone.qq.com/proxy/domain/taotao.qzone.qq.com/cgi-bin/emotion_cgi_publish_v6";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
    private readonly Func<CancellationToken, Task<string>> cookies;
    private readonly Action<string, object>? log;
    private readonly HttpClient http;

    public QzonePublisher(Func<CancellationToken, Task<string>> cookies, Action<string, object>? log = null,
        HttpMessageHandler? handler = null)
    {
        this.cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        this.log = log;
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        if (handler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseProxy = false; }
        if (handler is HttpClientHandler client) { client.AllowAutoRedirect = false; client.UseProxy = false; }
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task PublishAsync(string text, CancellationToken ct)
    {
        text = Validate(text);
        string jar;
        try { jar = await cookies(ct).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or OneBotMessageException)
        { throw new InvalidDataException("取不到空间登录凭证，这条说说没有发。", error); }
        var skey = QzoneCookie.Value(jar, "skey");
        var uin = QzoneCookie.Account(jar);
        if (string.IsNullOrWhiteSpace(skey) || uin is null)
            throw new InvalidDataException("空间登录凭证不完整，这条说说没有发。");
        var form = new Dictionary<string, string>
        {
            ["syn_tweet_verson"] = "1", ["paramstr"] = "1", ["who"] = "1", ["con"] = text,
            ["feedversion"] = "1", ["ver"] = "1", ["ugc_right"] = "1", ["secret"] = "0", ["to_sign"] = "0",
            ["hostuin"] = uin, ["code_v"] = "1", ["format"] = "json",
            ["qzreferrer"] = "https://user.qzone.qq.com/" + uin
        };
        var url = PublishUrl + "?g_tk=" + QzoneCookie.Gtk(skey).ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.TryAddWithoutValidation("Cookie", jar);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://user.qzone.qq.com/" + uin);
        string body;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Headers.Location is not null ||
                response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new InvalidDataException("空间接口没有接受这条说说。");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaximumResponseBytes) throw new InvalidDataException("空间接口返回内容过大。");
                output.Write(buffer, 0, count);
            }
            body = Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        { throw new InvalidDataException("空间接口没有响应，这条说说没有发。", error); }
        if (!Accepted(body)) throw new InvalidDataException("空间接口拒绝了这条说说。");
        log?.Invoke("qzone_publish", new { characters = text.Length });
    }

    /// <summary>接口返回的是 JSONP：frameElement.callback({...})。</summary>
    public static bool Accepted(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var start = body.IndexOf('(');
        var end = body.LastIndexOf(')');
        if (start < 0 || end <= start) return false;
        try
        {
            using var document = JsonDocument.Parse(body[(start + 1)..end], new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) && value == 0) return true;
            return root.TryGetProperty("err", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("code", out var nested) && nested.TryGetInt32(out var nestedValue) && nestedValue == 0;
        }
        catch (JsonException) { return false; }
    }

    public void Dispose() => http.Dispose();

    private static string Validate(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || text.Length > MaximumCharacters)
            throw new InvalidDataException("说说正文必须非空且不超过" + MaximumCharacters + "字。");
        if (text.Any(c => char.IsControl(c) && c is not '\n')) throw new InvalidDataException("说说正文包含无效字符。");
        return text;
    }
}

/// <summary>空间 cookie 与 g_tk；发布和读页面共用，避免两处各写一份。</summary>
public static class QzoneCookie
{
    public static string? Value(string jar, string name)
    {
        foreach (var part in (jar ?? "").Split(';'))
        {
            var trimmed = part.Trim();
            var index = trimmed.IndexOf('=');
            if (index > 0 && string.Equals(trimmed[..index], name, StringComparison.OrdinalIgnoreCase)) return trimmed[(index + 1)..];
        }
        return null;
    }

    /// <summary>空间用 skey 的 5381 散列；实测必须用原始 skey（含前导 @）。</summary>
    public static uint Gtk(string skey)
    {
        uint hash = 5381;
        foreach (var ch in skey) hash = hash * 33 + ch;
        return hash & 0x7fffffff;
    }

    /// <summary>cookie 里的 uin=o1667533356 → 1667533356。</summary>
    public static string? Account(string jar)
    {
        var uin = (Value(jar, "uin") ?? "").TrimStart('o');
        return uin.Length is > 0 and <= 20 && uin.All(char.IsAsciiDigit) ? uin : null;
    }
}
