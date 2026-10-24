using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioBridge;

public sealed record QqEmojiItem(string Id);
public sealed record QqEmojiAsset(byte[] Content, string MediaType);

/// <summary>Read-only access to the configured account's favorite and selected received images. Never sends or modifies QQ content.</summary>
public sealed class QqEmojiClient : IDisposable
{
    private const int MaximumItems = 48;
    private const int MaximumApiBytes = 256 * 1024;
    private readonly Uri origin;
    private readonly string expectedAccount;
    private readonly HttpClient api;
    private readonly HttpClient images;
    private readonly TimeProvider timeProvider;
    private readonly object gate = new();
    private Dictionary<string, Uri> catalog = new(StringComparer.Ordinal);
    private DateTimeOffset expires;

    public QqEmojiClient(string baseUrl, string token, long expectedSelfId,
        HttpMessageHandler? apiHandler = null, HttpMessageHandler? imageHandler = null, TimeProvider? timeProvider = null)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var url) || url.Scheme != "http" ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            url.AbsolutePath != "/" || baseUrl.Contains('\\')) throw new ArgumentException("收藏表情接口必须是本机回环 HTTP 根地址。");
        var host = url.DnsSafeHost.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) host = "127.0.0.1";
        if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address))
            throw new ArgumentException("收藏表情接口必须是本机回环 HTTP 根地址。");
        if (expectedSelfId <= 0) throw new ArgumentException("收藏表情必须绑定明确的机器人账号。");
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && !"-._~+/=".Contains(c)))
            throw new ArgumentException("请配置有效的本地 OneBot Token。");
        origin = new UriBuilder("http", address.ToString(), url.Port).Uri;
        expectedAccount = expectedSelfId.ToString(CultureInfo.InvariantCulture);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        api = Client(apiHandler);
        images = Client(imageHandler);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<IReadOnlyList<QqEmojiItem>> ListAsync(CancellationToken ct)
    {
        await VerifyAccountAsync(ct).ConfigureAwait(false);
        using var json = await CallAsync("fetch_custom_face", new { count = MaximumItems }, ct).ConfigureAwait(false);
        var data = json.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > MaximumItems) throw Failure();
        var next = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !TryImageUri(item.GetString(), out var uri)) throw Failure();
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant();
            next[id] = uri;
        }
        await VerifyAccountAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (gate) { catalog = next; expires = timeProvider.GetUtcNow().AddMinutes(10); }
        return next.Keys.Select(id => new QqEmojiItem(id)).ToArray();
    }

    public async Task<QqEmojiAsset> ReadAsync(string id, CancellationToken ct)
    {
        await VerifyAccountAsync(ct).ConfigureAwait(false);
        Uri uri;
        lock (gate)
        {
            if (timeProvider.GetUtcNow() >= expires || id is null || !catalog.TryGetValue(id, out uri!))
                throw new ArgumentException("收藏表情列表已过期或所选项不存在，请重新读取列表。");
        }
        var asset = await DownloadAsync(uri, ct).ConfigureAwait(false);
        await VerifyAccountAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return asset;
    }

    public async Task<QqEmojiAsset> ReadReceivedAsync(ReceivedImageInbox inbox, string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        if (inbox.ExpectedSelfId.ToString(CultureInfo.InvariantCulture) != expectedAccount)
            throw new ArgumentException("收到的图片必须来自当前配置的机器人账号。");
        await VerifyAccountAsync(ct).ConfigureAwait(false);
        var selection = inbox.Resolve(id);
        var asset = await DownloadAsync(selection.Url, ct).ConfigureAwait(false);
        await VerifyAccountAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        inbox.EnsureCurrent(selection);
        return asset;
    }

    private async Task<QqEmojiAsset> DownloadAsync(Uri uri, CancellationToken ct)
    {
        // All callers resolve an opaque selection previously admitted from authenticated QQ metadata.
        if (!TryImageUri(uri.AbsoluteUri, out _)) throw Failure();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        byte[] bytes;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await images.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            CheckResponse(response, uri, OneBotMessageClient.MaximumImageBytes);
            bytes = await BoundedAsync(response.Content, OneBotMessageClient.MaximumImageBytes, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or InvalidOperationException)
        { throw Failure(); }
        string mediaType;
        try { mediaType = MemeImageFormat.Validate(bytes); }
        catch (InvalidDataException) { throw Failure(); }
        ct.ThrowIfCancellationRequested();
        return new(bytes, mediaType);
    }

    private async Task VerifyAccountAsync(CancellationToken ct)
    {
        using var json = await CallAsync("get_login_info", new { }, ct).ConfigureAwait(false);
        var data = json.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("user_id", out var value)) throw Failure();
        var account = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (!string.Equals(account, expectedAccount, StringComparison.Ordinal))
        {
            lock (gate) { catalog.Clear(); expires = default; }
            throw new IOException("NapCat 当前账号与机器人配置不符，已拒绝读取收藏表情。");
        }
    }

    private async Task<JsonDocument> CallAsync(string action, object body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var target = new Uri(origin, action);
            using var request = new HttpRequestMessage(HttpMethod.Post, target)
            { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body)) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            CheckResponse(response, target, MaximumApiBytes);
            var bytes = await BoundedAsync(response.Content, MaximumApiBytes, deadline.Token).ConfigureAwait(false);
            var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count() ||
                !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "ok" ||
                !root.TryGetProperty("retcode", out var retcode) || !retcode.TryGetInt32(out var code) || code != 0 ||
                !root.TryGetProperty("data", out _)) { json.Dispose(); throw Failure(); }
            return json;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or JsonException or InvalidOperationException)
        { throw Failure(); }
    }

    internal static bool TryImageUri(string? text, out Uri uri)
    {
        uri = null!;
        if (text is null || text.Length > 8192 || text.Any(char.IsControl) || text.Contains('\\') ||
            !Uri.TryCreate(text, UriKind.Absolute, out var value) || value.Scheme != "https" || value.Port != 443 ||
            value.UserInfo.Length != 0 || value.Fragment.Length != 0 || value.HostNameType != UriHostNameType.Dns) return false;
        var host = value.DnsSafeHost;
        // 当前 QQ 客户端使用 multimedia.nt.qq.com.cn；旧客户端的 multimedia.nt.qq.com 一并保留，其余子域仍然拒绝。
        if (!host.Equals("qpic.cn", StringComparison.OrdinalIgnoreCase) && !host.EndsWith(".qpic.cn", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("multimedia.nt.qq.com.cn", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("multimedia.nt.qq.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))) return false;
        uri = value;
        return true;
    }

    private static void CheckResponse(HttpResponseMessage response, Uri target, int maximum)
    {
        if (!response.IsSuccessStatusCode || response.Headers.Location is not null ||
            response.RequestMessage?.RequestUri is { } actual && actual != target ||
            response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > maximum) throw Failure();
    }

    private static async Task<byte[]> BoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > maximum) throw Failure();
            output.Write(buffer, 0, count);
        }
        ct.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static HttpClient Client(HttpMessageHandler? handler)
    {
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        if (handler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseProxy = false; }
        if (handler is HttpClientHandler client) { client.AllowAutoRedirect = false; client.UseProxy = false; }
        return new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static IOException Failure() => new("无法安全读取 QQ 表情图片；请检查 NapCat 登录、网络或刷新列表。");
    public void Dispose() { api.Dispose(); images.Dispose(); }
}
