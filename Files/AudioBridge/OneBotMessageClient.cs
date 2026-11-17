using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AudioBridge;

public sealed record OneBotSegment(string Type, object Data)
{
    public static OneBotSegment Text(string text) => new("text", new { text });
    public static OneBotSegment Face(int id) => new("face", new { id });
    public static OneBotSegment Image(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        MemeImageFormat.Validate(bytes);
        return new("image", new { file = "base64://" + Convert.ToBase64String(bytes) });
    }
    public static OneBotSegment Record(byte[] wav)
    {
        ArgumentNullException.ThrowIfNull(wav);
        WaveForm.Validate(wav);
        return new("record", new { file = "base64://" + Convert.ToBase64String(wav) });
    }

    /// <summary>引用回复：引用的是群/私聊里那条真实消息的 id，只能放在消息最前面。</summary>
    public static OneBotSegment Reply(long messageId) => new("reply", new { id = messageId.ToString(CultureInfo.InvariantCulture) });
}

/// <summary>语音只接受本机合成的 16kHz/16位/单声道 PCM WAV，并限制在 60 秒以内。</summary>
internal static class WaveForm
{
    public static void Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 45 || bytes.Length > OneBotMessageClient.MaximumVoiceBytes ||
            !bytes.StartsWith("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8) ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length - 8 ||
            !bytes.Slice(12, 4).SequenceEqual("fmt "u8) ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) != 16 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]) != 1 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[22..]) != 1 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]) != LocalSpeechSynthesis.SampleRate ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[34..]) != 16 ||
            !bytes.Slice(36, 4).SequenceEqual("data"u8) ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]) != bytes.Length - 44)
            throw new InvalidDataException("语音只接受 16kHz/16位/单声道且不超过 60 秒的 WAV。");
    }
}

public sealed record OutgoingGroupMessage(string GroupId, IReadOnlyList<OneBotSegment> Segments);
public sealed record OutgoingPrivateMessage(string UserId, IReadOnlyList<OneBotSegment> Segments);
public sealed record OneBotMessageReceipt(string MessageId);
public sealed record OneBotOnlineStatus(bool Online, bool Good);

public sealed class OneBotMessageException(string message, bool deliveryMayHaveOccurred) : Exception(message)
{
    public bool DeliveryMayHaveOccurred { get; } = deliveryMayHaveOccurred;
}

/// <summary>Explicit local HTTP sends only. No retry, receive loop, or message-triggered file access.</summary>
public sealed class OneBotMessageClient : IDisposable
{
    public const int MaximumImageBytes = 5 * 1024 * 1024;
    public const int MaximumVoicePcmBytes = LocalSpeechSynthesis.SampleRate * 2 * 60;
    public const int MaximumVoiceBytes = 44 + MaximumVoicePcmBytes;
    public const int MaximumTextCharacters = 3000;
    public const int MaximumSegments = 4;
    private const int MaximumReplyBytes = 64 * 1024;
    private readonly Uri origin;
    private readonly HttpClient http;
    private readonly HashSet<string> groups;
    private readonly HashSet<string> privateUsers;

    public OneBotMessageClient(string baseUrl, string token, IEnumerable<string> allowedGroups,
        HttpMessageHandler? handler = null, IEnumerable<string>? allowedPrivateUsers = null)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var url) || url.Scheme != "http" ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            url.AbsolutePath != "/" || baseUrl.Contains('\\'))
            throw new ArgumentException("OneBot API 必须是本机回环 HTTP 根地址。");
        var host = url.DnsSafeHost.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) host = "127.0.0.1";
        if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address))
            throw new ArgumentException("OneBot API 必须是本机回环 HTTP 根地址。");
        origin = new UriBuilder("http", address.ToString(), url.Port).Uri;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && !"-._~+/=".Contains(c)))
            throw new ArgumentException("请配置有效的本地 OneBot Token。");
        ArgumentNullException.ThrowIfNull(allowedGroups);
        groups = allowedGroups.Select(GroupId).ToHashSet(StringComparer.Ordinal);
        if (groups.Count == 0) throw new ArgumentException("请配置至少一个明确的授权群号。");
        privateUsers = (allowedPrivateUsers ?? []).Select(GroupId).ToHashSet(StringComparer.Ordinal);
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        if (handler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseProxy = false; }
        if (handler is HttpClientHandler client) { client.AllowAutoRedirect = false; client.UseProxy = false; }
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<OneBotMessageReceipt> SendGroupAsync(OutgoingGroupMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var group = GroupId(message.GroupId);
        if (!groups.Contains(group)) throw new ArgumentException("该群不在明确授权的发送白名单中。");
        var segments = ValidateSegments(message.Segments);
        return await SendAsync("send_group_msg", new { group_id = group, message = segments, auto_escape = true }, ct).ConfigureAwait(false);
    }

    public async Task<OneBotMessageReceipt> SendPrivateAsync(OutgoingPrivateMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var user = GroupId(message.UserId);
        if (!privateUsers.Contains(user)) throw new ArgumentException("该联系人不在明确授权的私聊发送白名单中。");
        var segments = ValidateSegments(message.Segments);
        return await SendAsync("send_private_msg", new { user_id = user, message = segments, auto_escape = true }, ct).ConfigureAwait(false);
    }

    private static List<object> ValidateSegments(IReadOnlyList<OneBotSegment> messageSegments)
    {
        if (messageSegments is null || messageSegments.Count is < 1 or > MaximumSegments)
            throw new ArgumentException("一条消息只允许 1～4 个 text/face/image/reply 段。");
        var textCount = 0;
        var imageBytes = 0;
        var replies = 0;
        var segments = new List<object>();
        foreach (var segment in messageSegments)
        {
            if (segment is null || segment.Data is null) throw new ArgumentException("消息段无效。");
            JsonElement data;
            try { data = JsonSerializer.SerializeToElement(segment.Data); }
            catch { throw new ArgumentException("消息段数据无法验证。"); }
            if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().Count() != 1)
                throw new ArgumentException("消息段只能包含允许的数据字段。");
            object clean;
            switch (segment.Type)
            {
                case "text":
                    if (!data.TryGetProperty("text", out var textValue) || textValue.ValueKind != JsonValueKind.String)
                        throw new ArgumentException("text 段必须提供正文。");
                    var text = textValue.GetString()!;
                    textCount = checked(textCount + text.Length);
                    if (textCount > MaximumTextCharacters || text.Length == 0 || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                        throw new ArgumentException("正文必须非空且总计不超过 3000 字符。");
                    clean = new { text };
                    break;
                case "face":
                    if (!data.TryGetProperty("id", out var faceValue) || !TryInteger(faceValue, out var face) || face is < 0 or > 65535)
                        throw new ArgumentException("face 段必须提供有效表情编号。");
                    clean = new { id = face.ToString(CultureInfo.InvariantCulture) };
                    break;
                case "image":
                    if (!data.TryGetProperty("file", out var fileValue) || fileValue.ValueKind != JsonValueKind.String)
                        throw new ArgumentException("image 段仅接受已验证的内联图片。");
                    var file = fileValue.GetString()!;
                    if (!file.StartsWith("base64://", StringComparison.Ordinal) || file.Length - 9 > ((MaximumImageBytes + 2) / 3) * 4)
                        throw new ArgumentException("image 段仅接受最多 5 MiB 的内联图片。");
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(file[9..]); }
                    catch (FormatException) { throw new ArgumentException("图片编码无效。"); }
                    MemeImageFormat.Validate(bytes);
                    imageBytes = checked(imageBytes + bytes.Length);
                    if (imageBytes > MaximumImageBytes) throw new ArgumentException("每条消息的图片原始字节总计不得超过 5 MiB。");
                    clean = new { file = "base64://" + Convert.ToBase64String(bytes) };
                    break;
                case "record":
                    if (!data.TryGetProperty("file", out var voiceValue) || voiceValue.ValueKind != JsonValueKind.String)
                        throw new ArgumentException("record 段仅接受已验证的本机合成语音。");
                    var voice = voiceValue.GetString()!;
                    if (!voice.StartsWith("base64://", StringComparison.Ordinal) || voice.Length - 9 > ((MaximumVoiceBytes + 2) / 3) * 4)
                        throw new ArgumentException("record 段仅接受最多 60 秒的内联语音。");
                    byte[] wav;
                    try { wav = Convert.FromBase64String(voice[9..]); }
                    catch (FormatException) { throw new ArgumentException("语音编码无效。"); }
                    WaveForm.Validate(wav);
                    if (messageSegments.Count != 1) throw new ArgumentException("一条语音消息不能混入文字、表情或图片。");
                    clean = new { file = "base64://" + Convert.ToBase64String(wav) };
                    break;
                case "reply":
                    if (!data.TryGetProperty("id", out var replyValue) || replyValue.ValueKind != JsonValueKind.String)
                        throw new ArgumentException("reply 段必须提供有效的消息编号。");
                    var reply = replyValue.GetString() ?? "";
                    var digits = reply.StartsWith('-') ? reply[1..] : reply;
                    if (digits.Length is < 1 or > 19 || !digits.All(char.IsAsciiDigit))
                        throw new ArgumentException("reply 段必须提供有效的消息编号。");
                    // 引用必须排在最前面，且一条消息只引用一条。
                    if (++replies > 1 || segments.Count != 0) throw new ArgumentException("引用回复必须是最前面唯一的 reply 段。");
                    clean = new { id = reply };
                    break;
                default: throw new ArgumentException("只允许 text、face、image、record、reply 消息段。");
            }
            segments.Add(new { type = segment.Type, data = clean });
        }
        return segments;
    }

    private async Task<OneBotMessageReceipt> SendAsync(string action, object body, CancellationToken ct)
    {
        using var reply = await CallAsync(action, body, true, ct).ConfigureAwait(false);
        if (!reply.RootElement.GetProperty("data").TryGetProperty("message_id", out var id) ||
            !TryInteger(id, out var number) || number == 0)
            throw Failure(true);
        return new(number.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<string> GetLoginInfoAsync(CancellationToken ct)
    {
        using var reply = await CallAsync("get_login_info", new { }, false, ct).ConfigureAwait(false);
        if (!reply.RootElement.GetProperty("data").TryGetProperty("user_id", out var id)) throw Failure(false);
        try { return GroupId(id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText()); }
        catch (ArgumentException) { throw Failure(false); }
    }

    /// <summary>同意一条好友申请；flag 来自申请事件，只做格式校验后原样回传。</summary>
    public async Task ApproveFriendRequestAsync(string flag, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(flag) || flag.Length > 256 || flag.Any(char.IsControl))
            throw new ArgumentException("好友申请标识无效。");
        using var reply = await CallAsync("set_friend_add_request", new { flag, approve = true }, false, ct, allowNullData: true).ConfigureAwait(false);
    }

    /// <summary>取一条收到的语音并让 NapCat 转成 WAV，只返回本机存在的绝对路径。</summary>
    public async Task<string> GetRecordAsync(string file, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Length > 256 || file.Any(char.IsControl))
            throw new ArgumentException("语音文件标识无效。");
        using var reply = await CallAsync("get_record", new { file, out_format = "wav" }, false, ct).ConfigureAwait(false);
        if (!reply.RootElement.GetProperty("data").TryGetProperty("file", out var value) || value.ValueKind != JsonValueKind.String)
            throw Failure(false);
        var path = value.GetString() ?? "";
        if (path.Length is < 1 or > 512 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException("没有取得可读取的语音文件，可能在转换时失败了。");
        return Path.GetFullPath(path);
    }

    /// <summary>空间发布只用 qzone.qq.com 这一个域名的登录 cookie；失败时不含任何凭证内容。</summary>
    public async Task<string> GetQzoneCookiesAsync(CancellationToken ct)
    {
        using var reply = await CallAsync("get_cookies", new { domain = "qzone.qq.com" }, false, ct).ConfigureAwait(false);
        if (!reply.RootElement.GetProperty("data").TryGetProperty("cookies", out var value) || value.ValueKind != JsonValueKind.String)
            throw Failure(false);
        var cookies = value.GetString() ?? "";
        if (cookies.Length is < 1 or > 8192 || cookies.Any(c => char.IsControl(c)))
            throw new InvalidDataException("空间登录凭证无效，未发送。");
        return cookies;
    }

    public async Task<OneBotOnlineStatus> GetStatusAsync(CancellationToken ct)
    {
        using var reply = await CallAsync("get_status", new { }, false, ct).ConfigureAwait(false);
        var data = reply.RootElement.GetProperty("data");
        if (!data.TryGetProperty("online", out var online) || online.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !data.TryGetProperty("good", out var good) || good.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Failure(false);
        return new(online.GetBoolean(), good.GetBoolean());
    }

    private async Task<JsonDocument> CallAsync(string action, object body, bool sending, CancellationToken ct, bool allowNullData = false)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var attempted = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, action))
            { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body)) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            attempted = true;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Headers.Location is not null ||
                response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri ||
                response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength is > MaximumReplyBytes)
                throw Failure(sending);
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaximumReplyBytes) throw Failure(sending);
                output.Write(buffer, 0, count);
            }
            deadline.Token.ThrowIfCancellationRequested();
            var json = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count() ||
                !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
                status.GetString() != "ok" || !root.TryGetProperty("retcode", out var code) || !code.TryGetInt32(out var retcode) || retcode != 0 ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object && !(allowNullData && data.ValueKind == JsonValueKind.Null))
            { json.Dispose(); throw Failure(sending); }
            return json;
        }
        catch (OneBotMessageException) { throw; }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            // Do not retain transport exceptions: URL, request body or credential could be present.
            throw Failure(sending && attempted);
        }
    }

    private static OneBotMessageException Failure(bool uncertain) => new(uncertain
        ? "未取得有效的成功回执，不能保证消息未送达；请先核对聊天记录，勿自动重试。"
        : "未取得有效的 OneBot 登录信息；请检查本机接口和鉴权。", uncertain);

    private static bool TryInteger(JsonElement value, out long result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out result) :
            value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }

    private static string GroupId(string value)
    {
        if (value is null || value.Length is < 1 or > 20 || value.Any(c => c is < '0' or > '9') ||
            !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0)
            throw new ArgumentException("群号/账号必须是有效的正十进制编号。");
        return number.ToString(CultureInfo.InvariantCulture);
    }

    public void Dispose() => http.Dispose();
}
