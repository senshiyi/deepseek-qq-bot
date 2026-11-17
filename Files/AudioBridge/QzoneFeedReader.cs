using System.Text;

namespace AudioBridge;

public sealed record QzoneComment(string Id, string FeedTid, long FeedOwnerUin, long AuthorUin, string Author, string Text, string DisplayTime);
public sealed record QzoneFeed(string Tid, long OwnerUin, long PublishedAt, string Content, IReadOnlyList<QzoneComment> Comments);

/// <summary>
/// 空间页面是服务端渲染的：自己的说说和下面的评论都能直接从 HTML 读出来。
/// 只读一次页面、不调用任何空间 CGI（CGI 对非浏览器会话经常直接 500）。
/// </summary>
public sealed class QzoneFeedReader : IDisposable
{
    public const int MaximumHtmlBytes = 4 * 1024 * 1024;
    private const int MaximumFeedBlocks = 40;
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
    private readonly Func<CancellationToken, Task<string>> cookies;
    private readonly HttpClient http;

    public QzoneFeedReader(Func<CancellationToken, Task<string>> cookies, HttpMessageHandler? handler = null)
    {
        this.cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        if (handler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseProxy = false; }
        if (handler is HttpClientHandler client) { client.AllowAutoRedirect = false; client.UseProxy = false; }
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<IReadOnlyList<QzoneFeed>> ReadAsync(CancellationToken ct)
    {
        string jar;
        try { jar = await cookies(ct).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or OneBotMessageException)
        { throw new InvalidDataException("取不到空间登录凭证，这一轮没读到评论。", error); }
        var uin = QzoneCookie.Account(jar) ?? throw new InvalidDataException("空间登录凭证不完整，这一轮没读到评论。");
        string html;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://user.qzone.qq.com/" + uin);
            request.Headers.TryAddWithoutValidation("Cookie", jar);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", "https://user.qzone.qq.com/" + uin);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Headers.Location is not null ||
                response.Content.Headers.ContentLength > MaximumHtmlBytes)
                throw new InvalidDataException("空间页面没有正常返回，这一轮没读到评论。");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaximumHtmlBytes) throw new InvalidDataException("空间页面过大，这一轮没读到评论。");
                output.Write(buffer, 0, count);
            }
            html = Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        { throw new InvalidDataException("空间页面没有响应，这一轮没读到评论。", error); }
        return Parse(html);
    }

    public static IReadOnlyList<QzoneFeed> Parse(string html)
    {
        if (string.IsNullOrEmpty(html)) return [];
        var feeds = new Dictionary<string, QzoneFeed>(StringComparer.Ordinal);
        var order = new List<string>();
        var position = 0;
        for (var marked = 0; marked < MaximumFeedBlocks; marked++)
        {
            var start = html.IndexOf("data-topicid=\"", position, StringComparison.Ordinal);
            if (start < 0) break;
            var next = html.IndexOf("data-topicid=\"", start + 1, StringComparison.Ordinal);
            var length = (next < 0 ? html.Length : next) - start;
            position = next < 0 ? html.Length : next;
            var block = html.AsSpan(start, Math.Min(length, MaximumHtmlBytes));
            var text = block.ToString();
            if (!TryTopic(text, out var owner, out var tid)) continue;
            var published = FirstLong(text, "data-abstime=\"") ?? 0;
            var content = FeedContent(text);
            var comments = Comments(text, tid, owner);
            if (!feeds.TryGetValue(tid, out var existing)) { feeds[tid] = new(tid, owner, published, content, comments); order.Add(tid); continue; }
            // 同一条说说会在页面里出现多次（评论条目也带一份 feed_data）：合并内容与评论。
            feeds[tid] = existing with
            {
                Content = existing.Content.Length == 0 ? content : existing.Content,
                PublishedAt = existing.PublishedAt == 0 ? published : existing.PublishedAt,
                Comments = existing.Comments.Count == 0 ? comments : existing.Comments
            };
        }
        return order.Select(tid => feeds[tid]).ToArray();
    }

    private static bool TryTopic(string block, out long owner, out string tid)
    {
        owner = 0;
        tid = "";
        const string marker = "data-topicid=\"";
        var start = block.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return false;
        start += marker.Length;
        var end = block.IndexOf('"', start);
        if (end <= start) return false;
        var value = block[start..end];
        var separator = value.IndexOf('_');
        if (separator <= 0) return false;
        var second = value.IndexOf('_', separator + 1);
        if (second <= separator + 1) return false;
        if (!long.TryParse(value[..separator], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out owner) || owner <= 0) return false;
        tid = value[(separator + 1)..second];
        return tid.Length > 0;
    }

    private static long? FirstLong(string block, string marker)
    {
        var start = block.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = block.IndexOf('"', start);
        if (end <= start) return null;
        return long.TryParse(block[start..end], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string FeedContent(string block)
    {
        var start = block.IndexOf("class=\"txt-box-title", StringComparison.Ordinal);
        if (start < 0) return "";
        var end = block.IndexOf("</p>", start, StringComparison.Ordinal);
        var body = block[start..(end < 0 ? Math.Min(block.Length, start + 600) : end)];
        var anchor = body.IndexOf("</a>", StringComparison.Ordinal);
        var span = body.LastIndexOf("</span>", StringComparison.Ordinal);
        var cut = Math.Max(anchor < 0 ? 0 : anchor + 4, span < 0 ? 0 : span + 7);
        return Clean(body[cut..]);
    }

    private static IReadOnlyList<QzoneComment> Comments(string block, string tid, long owner)
    {
        var comments = new List<QzoneComment>();
        var position = 0;
        while (comments.Count < 40)
        {
            var start = block.IndexOf("class=\"comments-item", position, StringComparison.Ordinal);
            if (start < 0) break;
            var header = block.IndexOf('>', start);
            if (header < 0) break;
            var attributes = block[start..header];
            position = header + 1;
            if (!attributes.Contains("data-type=\"commentroot\"", StringComparison.Ordinal)) continue;
            var segment = block[position..Math.Min(block.Length, position + 1500)];
            var id = Attribute(attributes, "data-tid");
            var author = Attribute(attributes, "data-uin");
            var name = Attribute(attributes, "data-nick");
            if (id.Length == 0 || !long.TryParse(author, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var authorUin) || authorUin <= 0) continue;
            comments.Add(new(tid + ":" + id, tid, owner, authorUin, Clean(name), CommentText(segment), CommentTime(segment)));
        }
        return comments;
    }

    private static string CommentText(string segment)
    {
        var start = segment.IndexOf("</a>&nbsp;", StringComparison.Ordinal);
        var operation = segment.IndexOf("class=\"comments-op\"", StringComparison.Ordinal);
        if (start < 0 || operation < 0 || operation <= start) return "";
        var open = segment.LastIndexOf("<div", operation, StringComparison.Ordinal);
        var body = segment[(start + 4)..(open > start ? open : operation)];
        return Clean(body).TrimStart(':', '：', ' ');
    }

    private static string CommentTime(string segment)
    {
        var operation = segment.IndexOf("class=\"comments-op\"", StringComparison.Ordinal);
        if (operation < 0) return "";
        var tail = segment[operation..];
        var span = tail.IndexOf('>', tail.IndexOf("<span", StringComparison.Ordinal));
        if (span < 0) return "";
        var end = tail.IndexOf('<', span + 1);
        return end < 0 ? "" : Clean(tail[(span + 1)..end]);
    }

    private static string Attribute(string attributes, string name)
    {
        var start = attributes.IndexOf(name + "=\"", StringComparison.Ordinal);
        if (start < 0) return "";
        start += name.Length + 2;
        var end = attributes.IndexOf('"', start);
        return end <= start ? "" : attributes[start..end];
    }

    /// <summary>去掉标签、解常见实体、压空白；只用于展示给模型的一段普通文字。</summary>
    private static string Clean(string value)
    {
        var text = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch == '<')
            {
                var close = value.IndexOf('>', index);
                if (close < 0) break;
                index = close;
                text.Append(' ');
                continue;
            }
            text.Append(ch);
        }
        var decoded = text.ToString()
            .Replace("&nbsp;", " ", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&#39;", "'", StringComparison.Ordinal);
        return string.Join(' ', decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public void Dispose() => http.Dispose();
}
