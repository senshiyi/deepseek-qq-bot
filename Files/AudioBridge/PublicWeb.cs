using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AudioBridge;

public sealed record WebPage(string Url, string Title, string Text);

/// <summary>图片搜索的一条候选：标题只用来给收藏贴标签，地址仍要再走一次下载与结构校验。</summary>
public sealed record WebImage(string Title, string Url);

/// <summary>
/// 只读公网网页与图片。只允许 http/https 的公网地址（拒绝回环、内网、链路本地、运营商级 NAT），
/// 手动跟随最多两次重定向并逐次复核，限制字节与时间，图片必须通过结构校验。
/// ponytail: 域名只在预检时解析一次，未与实际连接 IP 绑定；要更严需在 socket 层比对。
/// </summary>
public sealed class PublicWeb : IDisposable
{
    public const int MaximumPageBytes = 2 * 1024 * 1024;
    public const int MaximumPageCharacters = 6000;
    public const int MaximumImageBytes = OneBotMessageClient.MaximumImageBytes;
    public const int MaximumSearchBytes = 512 * 1024;
    public const int MaximumSearchImages = 8;
    private const int MaximumRedirects = 2;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);
    private static readonly Regex TitlePattern = new("<title[^>]{0,200}>(.{0,400})</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
    private static readonly Regex CommentPattern = new("<!--.{0,20000}?-->", RegexOptions.Singleline, TimeSpan.FromSeconds(2));
    private static readonly Regex HiddenPattern = new("<(script|style|noscript|svg|head)[^>]{0,200}>.*?</\\1\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(3));
    private static readonly Regex BreakPattern = new("<(br|/p|/div|/li|/tr|/h[1-6]|/section|/article|/blockquote|/td)[^>]{0,80}>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    private static readonly Regex TagPattern = new("<[^>]{0,400}>", RegexOptions.Singleline, TimeSpan.FromSeconds(2));
    private static readonly Regex EntityPattern = new("&#(x?)([0-9a-fA-F]{1,6});", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex NullPattern = new("[\\u0000-\\u0008\\u000b\\u000c\\u000e-\\u001f\\u007f]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private readonly HttpClient http;
    private readonly Action<string, object>? log;

    public PublicWeb(Action<string, object>? log = null, HttpMessageHandler? handler = null)
    {
        this.log = log;
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            MaxResponseHeadersLength = 32
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<WebPage> ReadPageAsync(string url, CancellationToken ct)
    {
        var uri = await AdmitAsync(url, ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Deadline);
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DeepSeekQQBot/1.0");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,application/json;q=0.5");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (hop >= MaximumRedirects || response.Headers.Location is not { } location)
                    throw new InvalidDataException("这个网址重定向太多次或没有目标。");
                uri = await AdmitAsync(location.IsAbsoluteUri ? location.AbsoluteUri : new Uri(uri, location).AbsoluteUri,
                    deadline.Token).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new InvalidDataException("这个网址返回了 " + (int)response.StatusCode + "，打不开。");
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Length > 0 && !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
                mediaType is not ("application/xhtml+xml" or "application/json" or "application/xml" or "application/javascript"))
                throw new InvalidDataException("这个地址不是网页正文（" + Limit(mediaType, 40) + "）。");
            if (response.Content.Headers.ContentLength is > MaximumPageBytes) throw new InvalidDataException("这个网页太大了，我读不完。");
            var bytes = await ReadLimitedAsync(response, MaximumPageBytes, deadline.Token).ConfigureAwait(false);
            var html = Encoding.UTF8.GetString(bytes);
            if (html.Count(c => c == '\uFFFD') > html.Length / 20 && html.Length > 128)
                throw new InvalidDataException("这个网页不是我能读的文本编码，看不到内容。");
            var title = "";
            var text = Extract(html, ref title);
            if (text.Length < 8) throw new InvalidDataException("这个网页没有可以读的正文。");
            return new WebPage(uri.AbsoluteUri, title, text);
        }
    }

    /// <summary>用公开图片搜索找几张候选图；只返回地址，下载、主机预检和格式校验仍走 DownloadImageAsync。</summary>
    public async Task<IReadOnlyList<WebImage>> SearchImagesAsync(string query, CancellationToken ct)
    {
        var keyword = Clean(string.Join(" ", (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), 40);
        if (keyword.Length == 0) throw new InvalidDataException("搜索词是空的。");
        var uri = "https://image.so.com/j?q=" + Uri.EscapeDataString(keyword) + "&pn=0&rn=30&src=srp";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Deadline);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DeepSeekQQBot/1.0");
        request.Headers.Accept.ParseAdd("application/json,text/plain;q=0.9");
        // 这个接口没带 Referer 会被当成爬虫直接拒绝（body: {"status":1,"info":"forbid"}）。
        request.Headers.Referrer = new Uri("https://image.so.com/");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("图片搜索打不开（HTTP " + (int)response.StatusCode + "）。");
        var bytes = await ReadLimitedAsync(response, MaximumSearchBytes, deadline.Token).ConfigureAwait(false);
        log?.Invoke("image_search", new { keyword, bytes = bytes.Length });
        return ParseImages(bytes);
    }

    internal static IReadOnlyList<WebImage> ParseImages(byte[] json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { throw new InvalidDataException("图片搜索返回的不是 JSON。"); }
        using var scope = document;
        var root = scope.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("图片搜索返回的内容不认识。");
        var results = new List<WebImage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            // 尺寸太小的多半是图标或缩略图，当表情包会糊。
            if (item.TryGetProperty("width", out var widthValue) && widthValue.ValueKind == JsonValueKind.Number && widthValue.TryGetInt32(out var width) &&
                item.TryGetProperty("height", out var heightValue) && heightValue.ValueKind == JsonValueKind.Number && heightValue.TryGetInt32(out var height) &&
                (width < 96 || height < 96)) continue;
            var url = Text(item, "img") ?? Text(item, "thumb");
            if (url is null) continue;
            url = url.Replace("\\/", "/", StringComparison.Ordinal).Trim();
            if (url.Length is < 8 or > 2048 || url.Any(char.IsControl) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") ||
                parsed.UserInfo.Length != 0 || parsed.IsLoopback) continue;
            if (!seen.Add(parsed.AbsoluteUri)) continue;
            results.Add(new WebImage(Clean(Text(item, "title") ?? "", 40), parsed.AbsoluteUri));
            if (results.Count >= MaximumSearchImages) break;
        }
        return results;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public async Task<byte[]> DownloadImageAsync(string url, CancellationToken ct)
    {
        var uri = await AdmitAsync(url, ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Deadline);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DeepSeekQQBot/1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Headers.Location is not null)
            throw new InvalidDataException("这张图下载不下来。");
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Length > 0 && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("这个地址不是图片。");
        if (response.Content.Headers.ContentLength is > MaximumImageBytes) throw new InvalidDataException("这张图太大了。");
        var bytes = await ReadLimitedAsync(response, MaximumImageBytes, deadline.Token).ConfigureAwait(false);
        _ = MemeImageFormat.Validate(bytes);
        return bytes;
    }

    internal static async Task<Uri> AdmitAsync(string? value, CancellationToken ct)
    {
        if (value is null || value.Length > 2048 || value.Any(char.IsControl) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.Host.Length is < 3 or > 253 || uri.IsLoopback)
            throw new InvalidDataException("只接受公开的 http/https 网址。");
        var host = uri.DnsSafeHost.Trim('[', ']').ToLowerInvariant();
        if (host is "localhost" or "localhost.localdomain" || host.EndsWith(".local") || host.EndsWith(".internal") ||
            host.EndsWith(".lan") || host.EndsWith(".home.arpa") || host is "metadata" or "metadata.google.internal")
            throw new InvalidDataException("只接受公开的 http/https 网址。");
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (SocketException) { throw new InvalidDataException("这个网址的域名解析不了。"); }
        catch (ArgumentException) { throw new InvalidDataException("这个网址不合法。"); }
        if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address)))
            throw new InvalidDataException("只接受公网网址；本机、局域网和保留地址都不行。");
        return uri;
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                (address.GetAddressBytes()[0] & 0xfe) == 0xfc || address.Equals(IPAddress.IPv6None));
        var b = address.GetAddressBytes();
        return !(b[0] is 0 or 127 || b[0] == 10 || b[0] >= 224 ||
            b[0] == 169 && b[1] == 254 || b[0] == 172 && (b[1] & 0xf0) == 16 ||
            b[0] == 192 && b[1] == 168 || b[0] == 100 && (b[1] & 0xc0) == 64 ||
            b[0] == 192 && b[1] == 0 && b[2] is 0 or 2 || b[0] == 198 && (b[1] & 0xfe) == 18 ||
            b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        if (response.Content.Headers.ContentEncoding.Count != 0) throw new InvalidDataException("这个地址用了不支持的压缩方式。");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("内容超出读取上限。");
            output.Write(buffer, 0, count);
        }
        if (output.Length == 0) throw new InvalidDataException("这个地址没有内容。");
        return output.ToArray();
    }

    /// <summary>只做保守的正文抽取：去掉脚本/样式/标签，保留可读文字，不做渲染或执行。</summary>
    internal static string Extract(string html, ref string title)
    {
        try
        {
            var match = TitlePattern.Match(html);
            if (match.Success) title = Clean(Decode(match.Groups[1].Value), 120);
            var text = CommentPattern.Replace(html, " ");
            text = HiddenPattern.Replace(text, " ");
            text = BreakPattern.Replace(text, "\n");
            text = TagPattern.Replace(text, " ");
            return Clean(Decode(text), MaximumPageCharacters);
        }
        catch (RegexMatchTimeoutException) { return ""; }
    }

    private static string Decode(string value)
    {
        var text = value.Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
            .Replace("&#39;", "'", StringComparison.OrdinalIgnoreCase)
            .Replace("&apos;", "'", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
        return EntityPattern.Replace(text, m =>
        {
            if (!int.TryParse(m.Groups[2].Value, m.Groups[1].Value.Length == 0 ? System.Globalization.NumberStyles.None : System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var code) || code is < 32 or > 0x10FFFF ||
                code is >= 0xD800 and <= 0xDFFF) return " ";
            return char.ConvertFromUtf32(code);
        });
    }

    private static string Clean(string value, int maximum)
    {
        var text = NullPattern.Replace(value, " ");
        text = Regex.Replace(text, "[ \\t\\u00a0]+", " ");
        text = Regex.Replace(text, " ?\\n ?", "\n");
        text = Regex.Replace(text, "\\n{3,}", "\n\n");
        return Limit(text.Trim(), maximum);
    }

    private static string Limit(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        var cut = maximum;
        if (char.IsHighSurrogate(value[cut - 1])) cut--;
        return value[..cut] + "…";
    }

    public void Dispose() => http.Dispose();
}
