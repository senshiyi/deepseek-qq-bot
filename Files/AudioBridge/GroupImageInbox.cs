using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioBridge;

public enum ImageResolutionKind { None, Unique, Ambiguous }

public sealed record GroupImageEntry(string GroupId, string SenderId, string MessageId,
    DateTimeOffset Timestamp, Uri Url);

public sealed record GroupImageResolution(ImageResolutionKind Kind, IReadOnlyList<GroupImageEntry> Entries);

/// <summary>
/// Stores only authorized OneBot 11 image metadata. It never downloads images,
/// opens paths, executes messages, or assigns a mixed voice to a QQ sender.
/// </summary>
public sealed class GroupImageInbox
{
    public const int MaximumBodyBytes = 256 * 1024;
    public const int MaximumEntries = 20;
    public static readonly TimeSpan MaximumEventAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromSeconds(30);
    private readonly string groupId;
    private readonly string? selfId;
    private readonly HashSet<string> privateUsers;
    private readonly object gate = new();
    private readonly List<GroupImageEntry> entries = [];
    private readonly HashSet<string> conflictingMessages = [];
    private int started;
    private bool running;
    private string? lastError;

    public GroupImageInbox(string authorizedGroupId, string? authorizedSelfId = null, int port = 17800,
        IEnumerable<string>? allowedPrivateUsers = null)
    {
        groupId = NormalizeId(authorizedGroupId)
            ?? throw new ArgumentException("请配置明确的授权群号。", nameof(authorizedGroupId));
        selfId = authorizedSelfId is null ? null : NormalizeId(authorizedSelfId)
            ?? throw new ArgumentException("selfId 必须是有效 QQ 号。", nameof(authorizedSelfId));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        privateUsers = (allowedPrivateUsers ?? []).Select(value => NormalizeId(value)
            ?? throw new ArgumentException("私聊图片白名单只能包含有效 QQ 号。", nameof(allowedPrivateUsers)))
            .ToHashSet(StringComparer.Ordinal);
        Prefix = $"http://127.0.0.1:{port}/onebot/";
    }

    public string Prefix { get; }
    public bool IsRunning { get { lock (gate) return running; } }
    public string? LastError { get { lock (gate) return lastError; } }
    public int Count { get { lock (gate) return entries.Count; } }

    public bool AddEvent(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var raw = root.GetRawText();
        if (raw.Length > MaximumBodyBytes || Encoding.UTF8.GetByteCount(raw) > MaximumBodyBytes) return false;
        if (!IsString(root, "post_type", "message")) return false;
        if (!TryId(root, "user_id", out var sender) || !TryId(root, "message_id", out var message, signed: true)) return false;
        if (selfId is not null && (!TryId(root, "self_id", out var eventSelf) || eventSelf != selfId)) return false;
        // Group images key on the group; private images key on the authorized contact.
        string chatKey;
        if (IsString(root, "message_type", "group"))
        {
            if (!TryId(root, "group_id", out var eventGroup) || eventGroup != groupId) return false;
            chatKey = groupId;
        }
        else if (IsString(root, "message_type", "private"))
        {
            if (!privateUsers.Contains(sender!)) return false;
            chatKey = PrivateKey(sender!);
        }
        else return false;
        if (!root.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out var seconds)) return false;
        DateTimeOffset timestamp;
        try { timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return false; }
        if (now - timestamp > MaximumEventAge || timestamp - now > MaximumFutureSkew) return false;
        if (!root.TryGetProperty("message", out var segments) || segments.ValueKind != JsonValueKind.Array) return false;

        var images = new List<GroupImageEntry>();
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object || !IsString(segment, "type", "image")) continue;
            if (!segment.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
            JsonElement urlValue;
            if (!data.TryGetProperty("url", out urlValue) && !data.TryGetProperty("file", out urlValue)) return false;
            if (urlValue.ValueKind != JsonValueKind.String) return false;
            var value = urlValue.GetString();
            if (value is null || value.Length > 8192 || !Uri.TryCreate(value, UriKind.Absolute, out var url)
                || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
                || url.UserInfo.Length != 0 || url.IsLoopback) return false;
            if (images.All(x => x.Url != url)) images.Add(new(chatKey, sender!, message!, timestamp, url));
            if (images.Count > MaximumEntries) return false;
        }
        if (images.Count == 0) return false;

        lock (gate)
        {
            Prune(now);
            var previous = entries.Where(x => x.MessageId == message).ToArray();
            if (previous.Length != 0)
            {
                if (previous.Any(x => x.SenderId != sender || x.Timestamp != timestamp)
                    || !previous.Select(x => x.Url).ToHashSet().SetEquals(images.Select(x => x.Url)))
                    conflictingMessages.Add(message!);
                return false; // replay cannot replace metadata; a conflicting replay forces confirmation
            }
            while (entries.Count + images.Count > MaximumEntries)
            {
                var oldest = entries.MinBy(x => x.Timestamp)!;
                entries.RemoveAll(x => x.MessageId == oldest.MessageId);
                conflictingMessages.Remove(oldest.MessageId);
            }
            entries.AddRange(images);
        }
        return true;
    }

    public GroupImageResolution Resolve(string requestedChatKey, DateTimeOffset now, TimeSpan maxAge,
        DateTimeOffset? notBefore = null, IReadOnlySet<string>? excludedMessageIds = null)
    {
        if (maxAge <= TimeSpan.Zero || maxAge > MaximumEventAge)
            throw new ArgumentOutOfRangeException(nameof(maxAge), "图片关联时间必须在 0～5 分钟内。");
        var chatKey = NormalizeChatKey(requestedChatKey);
        if (chatKey is null) return new(ImageResolutionKind.None, []);
        lock (gate)
        {
            Prune(now);
            var candidates = entries.Where(x => x.GroupId == chatKey && now - x.Timestamp <= maxAge &&
                (notBefore is null || x.Timestamp >= notBefore) &&
                (excludedMessageIds is null || !excludedMessageIds.Contains(x.MessageId))).ToArray();
            var messages = candidates.Select(x => (x.MessageId, x.SenderId)).Distinct().Count();
            var kind = messages switch { 0 => ImageResolutionKind.None, 1 => ImageResolutionKind.Unique, _ => ImageResolutionKind.Ambiguous };
            if (candidates.Any(x => conflictingMessages.Contains(x.MessageId))) kind = ImageResolutionKind.Ambiguous;
            return new(kind, candidates);
        }
    }

    /// <summary>Lifetime is independent of voice. Startup/request faults are kept in LastError.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || Interlocked.CompareExchange(ref started, 1, 0) != 0) return;
        using var listener = new HttpListener();
        try
        {
            var token = Environment.GetEnvironmentVariable("ONEBOT_ACCESS_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("未设置 ONEBOT_ACCESS_TOKEN，图片 HTTP 入口保持关闭。");
            listener.Prefixes.Add(Prefix);
            listener.Start();
            lock (gate) { running = true; lastError = null; }
            using var stop = cancellationToken.Register(listener.Close);
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                await HandleAsync(context, token, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (cancellationToken.IsCancellationRequested && error is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception error)
        {
            lock (gate) lastError = error is HttpListenerException
                ? "图片入口无法监听；如缺少 URLACL，请手动处理权限后重启入口，本程序不会提权。" + error.Message
                : error.Message;
        }
        finally
        {
            lock (gate) running = false;
            Interlocked.Exchange(ref started, 0);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, string token, CancellationToken stopping)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (context.Request.HttpMethod != "POST") { context.Response.StatusCode = 405; return; }
            if (context.Request.Url?.AbsolutePath != "/onebot/") { context.Response.StatusCode = 404; return; }
            var authorization = context.Request.Headers["Authorization"] ?? "";
            var bearerValid = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization), Encoding.UTF8.GetBytes("Bearer " + token));
            var signature = ParseSignature(context.Request.Headers["X-Signature"]);
            if (!bearerValid && signature is null)
            { context.Response.StatusCode = 401; return; }
            if (context.Request.ContentLength64 > MaximumBodyBytes) { context.Response.StatusCode = 413; return; }

            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await context.Request.InputStream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (body.Length + read > MaximumBodyBytes) { context.Response.StatusCode = 413; return; }
                body.Write(buffer, 0, read);
            }
            var bytes = body.ToArray();
            // NapCat signs exactly the transmitted JSON bytes. Never normalize or
            // reserialize the body before validating this signature.
            if (!bearerValid && !CryptographicOperations.FixedTimeEquals(
                HMACSHA1.HashData(Encoding.UTF8.GetBytes(token), bytes), signature!))
            { context.Response.StatusCode = 401; return; }
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var accepted = AddEvent(document.RootElement, DateTimeOffset.UtcNow);
            context.Response.ContentType = "application/json; charset=utf-8";
            var response = Encoding.UTF8.GetBytes(accepted ? "{\"accepted\":true}" : "{\"accepted\":false}");
            await context.Response.OutputStream.WriteAsync(response, deadline.Token).ConfigureAwait(false);
        }
        catch (JsonException) { context.Response.StatusCode = 400; }
        catch (OperationCanceledException) { context.Response.StatusCode = 408; }
        catch (Exception error)
        {
            lock (gate) lastError = "图片请求未处理完成（" + error.GetType().Name + "）。";
            try { context.Response.StatusCode = 400; } catch (ObjectDisposedException) { }
        }
        finally { try { context.Response.Close(); } catch (ObjectDisposedException) { } }
    }

    private void Prune(DateTimeOffset now)
    {
        entries.RemoveAll(x => now - x.Timestamp > MaximumEventAge || x.Timestamp - now > MaximumFutureSkew);
        conflictingMessages.IntersectWith(entries.Select(x => x.MessageId));
    }
    private static byte[]? ParseSignature(string? value)
    {
        if (value is null || value.Length != 45 || !value.StartsWith("sha1=", StringComparison.Ordinal)) return null;
        try { return Convert.FromHexString(value.AsSpan(5)); }
        catch (FormatException) { return null; }
    }
    private static bool IsString(JsonElement element, string name, string expected) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == expected;
    private static string? NormalizeId(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id.ToString(CultureInfo.InvariantCulture) : null;
    public const string PrivateKeyPrefix = "private:";
    public static string PrivateKey(string userId) =>
        NormalizeId(userId) is { } user ? PrivateKeyPrefix + user : throw new ArgumentException("联系人QQ号必须是正整数。", nameof(userId));
    /// <summary>群号或 private:&lt;QQ&gt; 会话键；其他形式一律视为没有图片。</summary>
    private static string? NormalizeChatKey(string? value)
    {
        if (value is null) return null;
        if (!value.StartsWith(PrivateKeyPrefix, StringComparison.Ordinal)) return NormalizeId(value);
        var user = NormalizeId(value[PrivateKeyPrefix.Length..]);
        return user is null ? null : PrivateKeyPrefix + user;
    }
    private static bool TryId(JsonElement element, string name, out string? id, bool signed = false)
    {
        id = null;
        if (!element.TryGetProperty(name, out var value)) return false;
        var text = value.ValueKind switch { JsonValueKind.Number => value.GetRawText(), JsonValueKind.String => value.GetString(), _ => null };
        if (!signed) { id = NormalizeId(text); return id is not null; }
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) return false;
        id = number.ToString(CultureInfo.InvariantCulture);
        return true;
    }
}
