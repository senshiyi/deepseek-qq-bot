using System.Buffers.Binary;
using System.Globalization;
using System.Net;

namespace AudioBridge;

public sealed record InlineImage(string MimeType, string DataUrl)
{
    /// <summary>取出原始字节；不是 data:…;base64, 形式就返回 null，不猜。</summary>
    public byte[]? TryDecode()
    {
        const string marker = ";base64,";
        var index = DataUrl.IndexOf(marker, StringComparison.Ordinal);
        if (!DataUrl.StartsWith("data:", StringComparison.Ordinal) || index < 0) return null;
        try { return Convert.FromBase64String(DataUrl[(index + marker.Length)..]); }
        catch (FormatException) { return null; }
    }
}
public sealed record ImageQuestionContextResult(string? Clarification, IReadOnlyList<InlineImage> Images);

/// <summary>一轮回复拿到的图片资料：澄清请求、可直接附给能读图模型的图片，或给纯文字模型的转述。</summary>
public sealed record ImageContext(string? Clarification, IReadOnlyList<InlineImage> Images, string Caption)
{
    public static readonly ImageContext Empty = new(null, [], "");
}

/// <summary>能直读图片的回复实现；由 GroupChatBrain 在生成前附加本轮已授权的图片。</summary>
public interface IInlineImageSink
{
    void AttachImages(IReadOnlyList<InlineImage> images);
}

/// <summary>Resolves recent authorized QQ image metadata and fetches bounded still images into memory.</summary>
public sealed class ImageQuestionContext : IDisposable
{
    // 4 MiB / 4096 边 / 12 MP：覆盖手机截图（1080×2400 级）与常见照片；实测 4.12 MiB 图片、5.5 MiB 请求体可通过学校网关。
    public const int MaximumImageBytes = 4 * 1024 * 1024;
    public const int MaximumImageEdge = 4096;
    public const long MaximumImagePixels = 12_000_000;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(3);
    private static readonly string[] ImageTriggers = ["图片", "截图", "这张", "图里", "图中", "刚发", "这件装备", "看图"];
    private readonly GroupImageInbox inbox;
    private readonly string groupId;
    private readonly HttpClient http;
    private readonly TimeSpan timeout;
    private readonly object gate = new();
    private readonly Action<string>? rejectedHost;
    // 每个会话（群或私聊联系人）各自维护澄清游标与已看过的图片，避免串群和重复转述。
    private readonly Dictionary<string, ChatState> chats = new(StringComparer.Ordinal);

    private sealed class ChatState
    {
        public DateTimeOffset? AmbiguityAskedAt { get; set; }
        public HashSet<string> Excluded { get; set; } = [];
        public string? LastAttachedMessageId { get; set; }
    }

    public ImageQuestionContext(GroupImageInbox inbox, string groupId, HttpMessageHandler? handler = null,
        Action<string>? rejectedHost = null)
        : this(inbox, groupId, handler, TimeSpan.FromSeconds(5), rejectedHost) { }

    internal ImageQuestionContext(GroupImageInbox inbox, string groupId, HttpMessageHandler? handler, TimeSpan timeout,
        Action<string>? rejectedHost = null)
    {
        this.inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.groupId = groupId;
        this.timeout = timeout;
        this.rejectedHost = rejectedHost;
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxResponseHeadersLength = 16
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>语音入口：只有明确提到图片时才取当前群最近的图。</summary>
    public Task<ImageQuestionContextResult> ResolveAsync(string question, CancellationToken ct) =>
        ResolveAsync(question, DateTimeOffset.UtcNow, ct);

    internal Task<ImageQuestionContextResult> ResolveAsync(string question, DateTimeOffset now, CancellationToken ct) =>
        ResolveCore(groupId, 0, false, question, now, ct);

    /// <summary>
    /// 聊天入口：按会话取图。triggerMessageId 大于 0 时表示这是某条真实消息触发的回复，
    /// 最近一张还没看过的图会自动附上；为 0 时保持"提到图片才看"的旧行为。
    /// </summary>
    public Task<ImageQuestionContextResult> ResolveChatAsync(string chatKey, long triggerMessageId, bool triggerHasImage,
        string question, DateTimeOffset now, CancellationToken ct) =>
        ResolveCore(chatKey, triggerMessageId, triggerHasImage, question, now, ct);

    private async Task<ImageQuestionContextResult> ResolveCore(string chatKey, long triggerMessageId, bool triggerHasImage,
        string question, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);
        ct.ThrowIfCancellationRequested();
        var chat = ChatFor(chatKey);
        var keyword = ImageTriggers.Any(trigger => question.Contains(trigger, StringComparison.Ordinal));
        var auto = triggerMessageId > 0;
        GroupImageResolution selected;
        lock (gate)
        {
            selected = inbox.Resolve(chatKey, now, MaximumAge, chat.AmbiguityAskedAt, chat.Excluded);
            if (selected.Kind == ImageResolutionKind.None)
            {
                // 自动模式没图只是没得看，不能因为一条普通消息就去追问对方重发。
                return keyword || triggerHasImage
                    ? Clarify("我这边最近三分钟没看到图片，麻烦再发一次。")
                    : new(null, []);
            }
            var newest = selected.Entries.MaxBy(entry => (entry.Timestamp, ParseMessageId(entry.MessageId)))!;
            if (!triggerHasImage && !keyword && !auto) return new(null, []);
            // 同一张图只看一次，之后普通聊天不会重复下载或重复转述。
            if (auto && !triggerHasImage && !keyword && chat.LastAttachedMessageId == newest.MessageId) return new(null, []);
            if (selected.Kind == ImageResolutionKind.Ambiguous || selected.Entries.Count is < 1 or > 2)
            {
                // 触发消息自己带图时只看那条消息；自动模式取最新一张，像人一样先看刚发的。
                var narrowed = triggerHasImage
                    ? selected.Entries.Where(entry => ParseMessageId(entry.MessageId) == triggerMessageId).ToArray()
                    : auto ? selected.Entries.Where(entry => entry.MessageId == newest.MessageId).ToArray() : [];
                if (narrowed.Length is 1 or 2) selected = new GroupImageResolution(ImageResolutionKind.Unique, narrowed);
            }
            if (selected.Kind == ImageResolutionKind.Ambiguous || selected.Entries.Count is < 1 or > 2)
            {
                // OneBot timestamps have one-second precision. Exclude every message already seen,
                // while allowing a genuinely new message ID resent in the same second as the prompt.
                chat.AmbiguityAskedAt = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
                chat.Excluded = inbox.Resolve(chatKey, now, MaximumAge).Entries.Select(entry => entry.MessageId).ToHashSet();
                return Clarify("最近有好几张图，我分不清你指的是哪一张，只发那一张给我呗。");
            }
            chat.LastAttachedMessageId = selected.Entries.MaxBy(entry => (entry.Timestamp, ParseMessageId(entry.MessageId)))!.MessageId;
        }
        // The mixed call has no reliable member identity. Resolve never chooses by the voice's presumed sender.
        if (selected.Entries.FirstOrDefault(entry => !IsAllowedUrl(entry.Url)) is { } rejected)
        {
            // 只记主机名，便于排查 QQ 换了图片域名；路径和签名参数不落日志、不外露。
            rejectedHost?.Invoke(rejected.Url.Host);
            return Clarify("这张图我这边打不开，重发一张试试。");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var downloads = Task.WhenAll(selected.Entries.Select(entry =>
            Task.Run(() => DownloadAsync(entry.Url, deadline.Token))));
        // A misbehaving transport cannot keep the voice question waiting past its deadline.
        _ = downloads.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var images = await downloads.WaitAsync(timeout, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new(null, images);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or HttpRequestException or IOException or InvalidDataException)
        {
            // Never echo the transport message: it may contain a signed URL or token.
            _ = deadline.CancelAsync().ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return Clarify("这张图我读不出来，可能是太大了，压小一点再发一次。");
        }
    }

    /// <summary>要收藏表情时取最近一张已授权的图：不看"已经看过"的游标，但同样只接受白名单主机。</summary>
    public async Task<InlineImage?> ResolveNewestAsync(string chatKey, CancellationToken ct, TimeSpan? maximumAge = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatKey);
        ct.ThrowIfCancellationRequested();
        var age = maximumAge ?? MaximumAge;
        if (age <= TimeSpan.Zero || age > GroupImageInbox.MaximumEventAge) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        GroupImageResolution resolution;
        lock (gate) resolution = inbox.Resolve(chatKey, DateTimeOffset.UtcNow, age);
        var newest = resolution.Entries.MaxBy(entry => (entry.Timestamp, ParseMessageId(entry.MessageId)));
        if (newest is null) return null;
        if (!IsAllowedUrl(newest.Url))
        {
            rejectedHost?.Invoke(newest.Url.Host);
            return null;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        return await DownloadAsync(newest.Url, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>按消息ID取那条消息的图：给面板补转述用，不消耗"同一张只看一次"的游标。</summary>
    public async Task<IReadOnlyList<InlineImage>> ResolveMessageAsync(string chatKey, long messageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatKey);
        ct.ThrowIfCancellationRequested();
        GroupImageResolution resolution;
        lock (gate) resolution = inbox.Resolve(chatKey, DateTimeOffset.UtcNow, MaximumAge);
        var entries = resolution.Entries.Where(entry => ParseMessageId(entry.MessageId) == messageId).ToArray();
        if (entries.Length is < 1 or > 2) return [];
        if (entries.FirstOrDefault(entry => !IsAllowedUrl(entry.Url)) is { } rejected)
        {
            rejectedHost?.Invoke(rejected.Url.Host);
            return [];
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        return await Task.WhenAll(entries.Select(entry => Task.Run(() => DownloadAsync(entry.Url, deadline.Token))))
            .WaitAsync(timeout, ct).ConfigureAwait(false);
    }

    private ChatState ChatFor(string chatKey)
    {
        lock (gate)
        {
            if (!chats.TryGetValue(chatKey, out var state))
            {
                // Flat memory: at most one entry per authorized chat in a session.
                if (chats.Count >= 32) chats.Remove(chats.Keys.First());
                chats[chatKey] = state = new ChatState();
            }
            return state;
        }
    }

    private static long ParseMessageId(string value) => long.TryParse(value, NumberStyles.AllowLeadingSign,
        System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0;

    private static ImageQuestionContextResult Clarify(string message) => new(message, []);

    // 与收图/收藏共用同一份主机白名单；QQ 实际使用 multimedia.nt.qq.com.cn，不能再各写一套。
    private static bool IsAllowedUrl(Uri url) => QqEmojiClient.TryImageUri(url.AbsoluteUri, out _);

    private async Task<InlineImage> DownloadAsync(Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode || response.Headers.Location is not null ||
            response.Content.Headers.ContentEncoding.Count != 0 ||
            response.RequestMessage?.RequestUri is { } actual && actual != url)
            throw new InvalidDataException("图片下载响应不可用。");
        if (response.Content.Headers.ContentLength is > MaximumImageBytes or 0)
            throw new InvalidDataException("图片长度超出范围。");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > MaximumImageBytes) throw new InvalidDataException("图片长度超出范围。");
            output.Write(buffer, 0, count);
        }
        ct.ThrowIfCancellationRequested();
        var bytes = output.ToArray();
        var mime = ParseImage(bytes);
        return new(mime, $"data:{mime};base64,{Convert.ToBase64String(bytes)}");
    }

    private static string ParseImage(ReadOnlySpan<byte> bytes)
    {
        int width, height;
        string mime;
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            (width, height) = ParsePng(bytes);
            mime = "image/png";
        }
        else if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            (width, height) = ParseJpeg(bytes);
            mime = "image/jpeg";
        }
        else if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
        {
            // QQ 收藏表情包多为动图；网关和视觉模型都能读，只按首帧尺寸设上限。
            (width, height) = ParseGif(bytes);
            mime = "image/gif";
        }
        else if (bytes.Length >= 16 && bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            (width, height) = ParseWebp(bytes);
            mime = "image/webp";
        }
        else throw new InvalidDataException("仅支持 JPEG、PNG、GIF 或 WebP 图片。");
        if (width <= 0 || height <= 0)
        {
            // 只有 WebP 允许尺寸未知（仍受字节上限约束）；PNG/JPEG/GIF 的零尺寸是无效图片。
            if (mime == "image/webp") return mime;
            throw new InvalidDataException("图片尺寸无效。");
        }
        if (width > MaximumImageEdge || height > MaximumImageEdge || (long)width * height > MaximumImagePixels)
            throw new InvalidDataException("图片尺寸超出范围。");
        return mime;
    }

    private static (int Width, int Height) ParseGif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14 || bytes[^1] != 0x3b) throw new InvalidDataException("GIF 图片截断。");
        return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
    }

    private static (int Width, int Height) ParseWebp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 21) return (0, 0);
        var kind = bytes.Slice(12, 4);
        if (kind.SequenceEqual("VP8X"u8) && bytes.Length >= 30)
            return (1 + (bytes[24] | bytes[25] << 8 | bytes[26] << 16), 1 + (bytes[27] | bytes[28] << 8 | bytes[29] << 16));
        if (kind.SequenceEqual("VP8 "u8) && bytes.Length >= 30 && bytes[23] == 0x9d && bytes[24] == 0x01 && bytes[25] == 0x2a)
            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3fff,
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3fff);
        if (kind.SequenceEqual("VP8L"u8) && bytes.Length >= 25 && bytes[20] == 0x2f)
        {
            var packed = (uint)(bytes[21] | bytes[22] << 8 | bytes[23] << 16 | bytes[24] << 24);
            return ((int)(packed & 0x3fff) + 1, (int)((packed >> 14) & 0x3fff) + 1);
        }
        return (0, 0);
    }

    private static (int Width, int Height) ParsePng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 45 || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("PNG 图片头无效。");
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
        if (width > int.MaxValue || height > int.MaxValue || bytes[26] != 0 || bytes[27] != 0 || bytes[28] > 1 ||
            !ValidPngDepth(bytes[24], bytes[25])) throw new InvalidDataException("PNG 图片尺寸或格式无效。");
        var position = 8;
        var hasData = false;
        while (position <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[position..]);
            if (length > (uint)(bytes.Length - position - 12)) throw new InvalidDataException("PNG 图片截断。");
            var size = (int)length;
            var kind = bytes.Slice(position + 4, 4);
            var checksum = BinaryPrimitives.ReadUInt32BigEndian(bytes[(position + 8 + size)..]);
            if (PngCrc(bytes.Slice(position + 4, size + 4)) != checksum ||
                position != 8 && kind.SequenceEqual("IHDR"u8) || kind.SequenceEqual("acTL"u8))
                throw new InvalidDataException("PNG 图片结构无效。");
            if (kind.SequenceEqual("IDAT"u8) && size > 0) hasData = true;
            position += size + 12;
            if (kind.SequenceEqual("IEND"u8))
            {
                if (size == 0 && hasData && position == bytes.Length) return ((int)width, (int)height);
                throw new InvalidDataException("PNG 图片结束标记无效。");
            }
        }
        throw new InvalidDataException("PNG 图片缺少结束标记。");
    }

    private static bool ValidPngDepth(byte depth, byte color) => color switch
    {
        0 => depth is 1 or 2 or 4 or 8 or 16,
        2 or 4 or 6 => depth is 8 or 16,
        3 => depth is 1 or 2 or 4 or 8,
        _ => false
    };

    private static uint PngCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }

    private static (int Width, int Height) ParseJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes[^2] != 0xff || bytes[^1] != 0xd9) throw new InvalidDataException("JPEG 图片截断。");
        var position = 2;
        var width = 0;
        var height = 0;
        while (position < bytes.Length - 2)
        {
            if (bytes[position++] != 0xff) throw new InvalidDataException("JPEG 图片标记无效。");
            while (position < bytes.Length && bytes[position] == 0xff) position++;
            if (position >= bytes.Length) break;
            var marker = bytes[position++];
            if (marker is 0x00 or 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7)
                throw new InvalidDataException("JPEG 图片标记无效。");
            if (marker == 0x01) continue;
            if (position > bytes.Length - 2) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]);
            if (length < 2 || position + length > bytes.Length - 2) throw new InvalidDataException("JPEG 图片片段截断。");
            var frame = marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc);
            if (frame)
            {
                if (length < 11 || bytes[position + 7] is < 1 or > 4 || length != 8 + bytes[position + 7] * 3)
                    throw new InvalidDataException("JPEG 图片尺寸字段无效。");
                var nextHeight = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 3)..]);
                var nextWidth = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 5)..]);
                if (width != 0 && (width != nextWidth || height != nextHeight)) throw new InvalidDataException("JPEG 多帧尺寸不一致。");
                width = nextWidth;
                height = nextHeight;
            }
            if (marker == 0xda)
            {
                if (width == 0 || height == 0 || length < 6 || position + length >= bytes.Length - 2)
                    throw new InvalidDataException("JPEG 图片缺少有效数据。");
                return (width, height);
            }
            position += length;
        }
        throw new InvalidDataException("JPEG 图片缺少尺寸或扫描数据。");
    }

    public void Dispose() => http.Dispose();
}
