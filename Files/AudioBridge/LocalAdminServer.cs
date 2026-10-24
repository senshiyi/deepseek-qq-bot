using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioBridge;

/// <summary>Loopback-only administration. Never accepts a command, model key or arbitrary filesystem path.</summary>
public sealed partial class LocalAdminServer
{
    public const int DefaultPort = 17802;
    private const int MaximumBodyBytes = 8 * 1024 * 1024;
    private readonly string project, token, origin;
    private readonly BotProfileStore profiles;
    private readonly ContactContextStore contacts;
    private readonly MemeLibrary memes;
    private readonly ReceivedImageInbox? receivedImages;
    private ScheduledMessageStore? scheduledMessages;
    private readonly object scheduleGate = new();
    private readonly Action<string>? scheduleChanged;
    private readonly Func<QqEmojiClient>? qqEmojiFactory;
    private QqEmojiClient? qqEmojis;
    private readonly Func<object> status;
    private readonly Func<string, Task> control;
    private readonly object settingsGate = new();
    private readonly object qqEmojiGate = new();
    private readonly SemaphoreSlim favoriteDownloads = new(2, 2);
    private readonly SemaphoreSlim controls = new(1, 1);
    private bool restartRequired;
    public string Prefix { get; }
    public bool IsRunning { get; private set; }

    public LocalAdminServer(string project, string token, Func<object> status, Func<string, Task> control, int port = DefaultPort,
        Func<QqEmojiClient>? qqEmojiFactory = null, ReceivedImageInbox? receivedImages = null,
        ScheduledMessageStore? scheduledMessages = null, Action<string>? scheduleChanged = null,
        Func<string, IStreamingReply>? personaReplyFactory = null)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (token.Length != 64 || token.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("面板口令格式无效。");
        this.project = Path.GetFullPath(project); this.token = token; this.status = status; this.control = control;
        origin = $"http://127.0.0.1:{port}"; Prefix = origin + "/";
        profiles = new(project); contacts = new(project); memes = new(project); companion = new(project);
        this.qqEmojiFactory = qqEmojiFactory;
        this.receivedImages = receivedImages;
        this.scheduledMessages = scheduledMessages;
        this.scheduleChanged = scheduleChanged;
        this.personaReplyFactory = personaReplyFactory;
    }

    public void Applied() { lock (settingsGate) restartRequired = false; }

    public static string LoadOrCreateToken(string project)
    {
        var directory = Path.GetFullPath(Path.Combine(project, "Results", "local"));
        EnsureNoLinks(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "admin-access-token.txt");
        EnsureNoLinks(path);
        if (!File.Exists(path))
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));
        }
        var result = File.ReadAllText(path).Trim();
        if (result.Length != 64 || result.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("面板本地口令文件无效，不自动覆盖。");
        return result;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var listener = new HttpListener(); listener.Prefixes.Add(Prefix); listener.Start(); IsRunning = true;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var close = stopping.Token.Register(listener.Close);
        using var slots = new SemaphoreSlim(16, 16);
        var handlers = new List<Task>();
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                handlers.RemoveAll(task => { if (!task.IsCompleted) return false; _ = task.Exception; return true; });
                if (!slots.Wait(0))
                {
                    // Never queue unbounded work or hold the accept loop on an overloaded response body.
                    try
                    {
                        context.Response.StatusCode = 503;
                        context.Response.Headers["Cache-Control"] = "no-store";
                        context.Response.Headers["Retry-After"] = "1";
                        context.Response.ContentLength64 = 0;
                        context.Response.Close();
                    }
                    catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or IOException) { }
                    continue;
                }
                // Local store locks can block briefly; they must not run on the accept-loop continuation.
                handlers.Add(Task.Run(async () =>
                {
                    try { await HandleAsync(context, stopping.Token).ConfigureAwait(false); }
                    finally { slots.Release(); }
                }, CancellationToken.None));
            }
        }
        catch (Exception e) when (stopping.IsCancellationRequested && e is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            IsRunning = false;
            await stopping.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(handlers).ConfigureAwait(false); }
            finally { lock (qqEmojiGate) { qqEmojis?.Dispose(); qqEmojis = null; } }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var response = context.Response;
        try
        {
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            var request = context.Request;
            if (request.RemoteEndPoint is null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) ||
                request.Headers["Host"] != new Uri(origin).Authority ||
                request.Headers["Origin"] is { } source && source != origin)
            { await JsonAsync(response, new { error = "只允许本机同源访问。" }, 403, deadline.Token); return; }
            var path = request.Url!.AbsolutePath;
            if (path.StartsWith("/api/qq-favorites", StringComparison.Ordinal) ||
                path.StartsWith("/api/received-images", StringComparison.Ordinal)) deadline.CancelAfter(TimeSpan.FromSeconds(20));
            if (path == "/api/persona-learning/generate") deadline.CancelAfter(TimeSpan.FromSeconds(120));
            if (path == "/api/memes/batch") deadline.CancelAfter(TimeSpan.FromSeconds(30));
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                if (request.HttpMethod != "GET") { response.StatusCode = 405; return; }
                if (path == "/favicon.ico") { response.StatusCode = 204; return; }
                var file = path switch { "/" or "/index.html" => "index.html", "/app.css" => "app.css", "/app.js" => "app.js", "/companion.js" => "companion.js", _ => null };
                if (file is null) { response.StatusCode = 404; return; }
                using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("AudioBridge.AdminWeb." + file);
                if (resource is null) { response.StatusCode = 503; return; }
                response.ContentType = file.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : file.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript; charset=utf-8" : "text/html; charset=utf-8";
                await resource.CopyToAsync(response.OutputStream, deadline.Token); return;
            }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Headers["Authorization"] ?? ""), Encoding.UTF8.GetBytes("Bearer " + token)))
            { await JsonAsync(response, new { error = "请使用本机面板入口登录。" }, 401, deadline.Token); return; }
            if (request.HttpMethod == "GET") { await GetAsync(context, path, deadline.Token); return; }
            if (request.HttpMethod == "DELETE") { await DeleteAsync(context, path, deadline.Token); return; }
            if (request.HttpMethod is not ("POST" or "PUT")) { response.StatusCode = 405; return; }
            using var json = await ReadJsonAsync(request, deadline.Token,
                path == "/api/memes/batch" ? 30 * 1024 * 1024 : MaximumBodyBytes);
            await SaveAsync(context, path, json.RootElement, deadline.Token);
        }
        catch (OperationCanceledException) { await ErrorAsync(response, "面板请求超时，请稍后重试。", 408); }
        catch (JsonException) { await ErrorAsync(response, "配置不是有效的JSON数据。", 400); }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { await ErrorAsync(response, "输入不符合要求，请检查编号、长度、标签和配置范围。", 400); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        { await ErrorAsync(response, "本地数据暂时无法读写；原数据保留，请检查文件或稍后重试。", 503); }
        catch { await ErrorAsync(response, "操作未完成，请检查本地服务状态。", 500); }
        finally { try { response.Close(); } catch (Exception error) when (error is ObjectDisposedException or HttpListenerException or IOException) { } }
    }

    private async Task GetAsync(HttpListenerContext context, string path, CancellationToken ct)
    {
        object result;
        var request = context.Request;
        switch (path)
        {
            case "/api/status":
                bool dirty; lock (settingsGate) dirty = restartRequired;
                result = new { runtime = status(), restartRequired = dirty }; break;
            case "/api/persona": result = profiles.GetPersona(); break;
            case "/api/schedules": result = Schedules().List(); break;
            case "/api/settings":
                lock (settingsGate) result = GroupChatSettings.Load(Path.Combine(project, "group-chat.local.json")); break;
            case "/api/memories": result = profiles.ListMemories(UserId(request.QueryString["userId"])); break;
            case "/api/memory-suggestions":
                var contact = UserId(request.QueryString["userId"]);
                result = MemorySuggestions.ForContact(contact, contacts.Recent(contact, 1000), profiles.ListMemories(contact)); break;
            case "/api/history": result = contacts.Recent(UserId(request.QueryString["userId"])); break;
            case "/api/public-preferences": result = contacts.GetPublicPreference(UserId(request.QueryString["userId"])); break;
            case "/api/companion": result = CompanionList(); break;
            case "/api/companion-settings": result = companion.LoadSettings(); break;
            case "/api/memes":
                result = memes.List().Select(item => new { item.Id, item.Tags, item.FaceId,
                    previewUrl = item.FaceId is null ? "/api/memes/" + item.Id + "/image" : null,
                    mediaType = item.FaceId is not null ? "qq/face" : item.File.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif" : "image" }); break;
            case "/api/qq-favorites":
                result = (await QqEmojis().ListAsync(ct)).Select(item => new { item.Id,
                    previewUrl = "/api/qq-favorites/" + item.Id + "/image" }); break;
            case "/api/received-images":
                result = (receivedImages?.List() ?? []).Where(ReceivedImageAllowed).Select(item => new {
                    item.Id, item.SenderId, item.GroupId, item.MessageId, item.At, item.IsPrivate,
                    previewUrl = "/api/received-images/" + item.Id + "/image" }); break;
            default:
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts is ["api", "memes", var id, "image"])
                {
                    var asset = memes.Get(id); context.Response.ContentType = asset.MediaType;
                    await context.Response.OutputStream.WriteAsync(asset.Content, ct); return;
                }
                if (parts is ["api", "qq-favorites", var favoriteId, "image"])
                {
                    var asset = await ReadFavoriteAsync(favoriteId, ct); context.Response.ContentType = asset.MediaType;
                    await context.Response.OutputStream.WriteAsync(asset.Content, ct); return;
                }
                if (parts is ["api", "received-images", var receivedId, "image"])
                {
                    var asset = await ReadReceivedAsync(receivedId, ct); context.Response.ContentType = asset.MediaType;
                    await context.Response.OutputStream.WriteAsync(asset.Content, ct); return;
                }
                context.Response.StatusCode = 404; return;
        }
        await JsonAsync(context.Response, result, 200, ct);
    }

    private async Task SaveAsync(HttpListenerContext context, string path, JsonElement body, CancellationToken ct)
    {
        object result;
        var method = context.Request.HttpMethod;
        if (path == "/api/control" && method == "POST")
        {
            var action = body.GetProperty("action").GetString();
            if (action is not ("pause" or "resume" or "apply" or "stop")) throw new ArgumentException();
            // The host's control callback has a single writer; downloads never own this gate.
            await controls.WaitAsync(ct).ConfigureAwait(false);
            try { ct.ThrowIfCancellationRequested(); await control(action).ConfigureAwait(false); }
            finally { controls.Release(); }
            await JsonAsync(context.Response, new { accepted = true, action }, 200, ct); return;
        }
        if (path.StartsWith("/api/persona-learning/", StringComparison.Ordinal) && method == "POST")
        {
            await PersonaLearningAsync(context, path, body, ct); return;
        }
        if (path == "/api/memes/batch" && method == "POST")
            result = new MemeBatchImporter(memes).Import(Deserialize<MemeBatchImportRequest>(body), ct);
        else if (path == "/api/schedules" && method == "POST")
        {
            var draft = Deserialize<ScheduledMessageDraft>(body);
            result = SaveSchedule(draft);
        }
        else if (path.StartsWith("/api/schedules/", StringComparison.Ordinal) && method == "PUT")
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts is not ["api", "schedules", var scheduleId]) throw new ArgumentException();
            var draft = Deserialize<ScheduledMessageDraft>(body.GetProperty("definition"));
            result = SaveSchedule(draft, scheduleId, body.GetProperty("revision").GetInt64());
        }
        else if (path == "/api/persona" && method == "PUT")
        { profiles.SavePersona(Deserialize<PersonaSettings>(body)); MarkDirty(); result = profiles.GetPersona(); }
        else if (path == "/api/settings" && method == "PUT")
        {
            var value = Deserialize<GroupChatSettings>(body); value.Validate();
            lock (settingsGate)
            {
                var file = Path.Combine(project, "group-chat.local.json");
                var current = GroupChatSettings.Load(file);
                if (value.SelfId != current.SelfId || value.OneBotBaseUrl != current.OneBotBaseUrl || value.EventPort != current.EventPort || value.MemeDirectory != current.MemeDirectory)
                    throw new ArgumentException("面板不修改接入账号、接口端口或目录。");
                var backup = Path.Combine(project, "Results", "local", "group-chat.panel-before-" + Guid.NewGuid().ToString("N") + ".json");
                EnsureNoLinks(file); EnsureNoLinks(Path.GetDirectoryName(backup)!);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(file, backup, false);
                var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, BridgeConfig.JsonOptions)); File.Move(temporary, file, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                restartRequired = true;
            }
            result = value;
        }
        else if (path == "/api/memories" && method == "POST")
            result = profiles.AddMemory(body.GetProperty("userId").GetInt64(), body.GetProperty("text").GetString()!);
        else if (path == "/api/companion-settings" && method == "PUT")
        { await SaveCompanionSettingsAsync(context, body, ct); return; }
        else if (path == "/api/public-preferences" && method == "PUT")
        { var preference = Deserialize<PublicContactPreference>(body); contacts.SavePublicPreference(preference); result = preference; }
        else if (path == "/api/memes" && method == "POST")
        {
            var encoded = body.GetProperty("base64").GetString() ?? "";
            if (encoded.Length > ((OneBotMessageClient.MaximumImageBytes + 2) / 3) * 4) throw new ArgumentException();
            var asset = memes.ImportBytes(body.GetProperty("id").GetString()!, Convert.FromBase64String(encoded), Tags(body));
            result = new { asset.Id, asset.Tags, asset.MediaType };
        }
        else if (path == "/api/qq-faces" && method == "POST")
        { result = memes.AddFace(body.GetProperty("faceId").GetInt32(), Tags(body)); }
        else if (path == "/api/qq-favorites/import" && method == "POST")
        {
            var asset = await ReadFavoriteAsync(body.GetProperty("favoriteId").GetString()!, ct);
            var imported = memes.ImportBytes(body.GetProperty("id").GetString()!, asset.Content, Tags(body));
            result = new { imported.Id, imported.Tags, imported.MediaType };
        }
        else if (path == "/api/received-images/import" && method == "POST")
        {
            if (!body.TryGetProperty("reuseApproved", out var approval) || approval.ValueKind != JsonValueKind.True)
                throw new ArgumentException("请先确认所选图片可在所有授权会话中作为表情复用。");
            var asset = await ReadReceivedAsync(body.GetProperty("receivedId").GetString()!, ct);
            var imported = memes.ImportBytes(body.GetProperty("id").GetString()!, asset.Content, Tags(body));
            result = new { imported.Id, imported.Tags, imported.MediaType };
        }
        else
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (method == "PUT" && parts is ["api", "memories", var memoryId])
                result = profiles.UpdateMemory(body.GetProperty("userId").GetInt64(), memoryId, body.GetProperty("text").GetString()!);
            else if (method == "PUT" && parts is ["api", "memes", var memeId])
            { result = memes.UpdateTags(memeId, Tags(body)); }
            else { context.Response.StatusCode = 404; return; }
        }
        await JsonAsync(context.Response, result, 200, ct);
    }

    private async Task DeleteAsync(HttpListenerContext context, string path, CancellationToken ct)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["api", "memes", var memeId])
        {
            var deleted = memes.Delete(memeId);
            await JsonAsync(context.Response, new { deleted, originalFileRetained = true }, 200, ct); return;
        }
        if (parts is ["api", "schedules", var scheduleId])
        {
            if (!long.TryParse(context.Request.QueryString["revision"], out var revision) || revision <= 0) throw new ArgumentException();
            var deleted = Schedules().Delete(scheduleId, revision);
            scheduleChanged?.Invoke(scheduleId);
            await JsonAsync(context.Response, new { deleted }, 200, ct); return;
        }
        if (parts is ["api", "companion", var companionUserId, "entries", var companionEntryId])
        {
            await DeleteCompanionEntryAsync(context, UserId(companionUserId), companionEntryId, ct); return;
        }
        var id = UserId(context.Request.QueryString["userId"]);
        if (parts is ["api", "memories", var memoryId])
            await JsonAsync(context.Response, new { deleted = profiles.DeleteMemory(id, memoryId) }, 200, ct);
        else if (path == "/api/history")
        { contacts.DeleteHistory(id); await JsonAsync(context.Response, new { deleted = true }, 200, ct); }
        else context.Response.StatusCode = 404;
    }

    private void MarkDirty() { lock (settingsGate) restartRequired = true; }
    private ScheduledMessageTask SaveSchedule(ScheduledMessageDraft draft, string? id = null, long? revision = null)
    {
        lock (settingsGate)
        {
            var current = GroupChatSettings.Load(Path.Combine(project, "group-chat.local.json"));
            var selected = current.ActiveGroupId;
            if (selected == 0)
            {
                // A legacy default follows the effective selected group, rather than authorizing every listed group.
                var runtime = JsonSerializer.SerializeToElement(status(), BridgeConfig.JsonOptions);
                selected = runtime.TryGetProperty("groupId", out var group) && group.TryGetInt64(out var runningGroup)
                    ? runningGroup : current.AllowedGroupIds[0];
            }
            if (draft.MemeId is { } meme) _ = memes.ResolveSegment(meme);
            var saved = Schedules().Save(draft, current.AllowedGroupIds.Where(group => group == selected),
                current.AllowedPrivateUserIds, id, revision);
            scheduleChanged?.Invoke(saved.Id);
            return saved; // CRUD is live; it does not replace the chat session or reset its pending messages.
        }
    }
    private ScheduledMessageStore Schedules()
    { lock (scheduleGate) return scheduledMessages ??= new ScheduledMessageStore(project); }
    private bool ReceivedImageAllowed(ReceivedImageCandidate item)
    {
        GroupChatSettings current;
        lock (settingsGate) current = GroupChatSettings.Load(Path.Combine(project, "group-chat.local.json"));
        return item.IsPrivate ? current.AllowedPrivateUserIds.Contains(item.SenderId) :
            current.AllowedGroupIds.Contains(item.GroupId) &&
            (current.ActiveGroupId == 0 || current.ActiveGroupId == item.GroupId);
    }
    private async Task<QqEmojiAsset> ReadReceivedAsync(string id, CancellationToken ct)
    {
        var inbox = receivedImages ?? throw new ArgumentException("本次宿主未提供收到图片入口。");
        bool Allowed() => inbox.List().Any(item => item.Id == id && ReceivedImageAllowed(item));
        if (!Allowed()) throw new ArgumentException("收到的图片已过期或来源不在当前授权范围，请刷新列表。");
        if (!await favoriteDownloads.WaitAsync(0, ct).ConfigureAwait(false)) throw new TimeoutException("图片预览繁忙，请稍后重试。");
        try
        {
            var asset = await QqEmojis().ReadReceivedAsync(inbox, id, ct).ConfigureAwait(false);
            if (!Allowed()) throw new ArgumentException("图片来源授权已改变或候选已过期，未导入。");
            return asset;
        }
        finally { favoriteDownloads.Release(); }
    }
    private async Task<QqEmojiAsset> ReadFavoriteAsync(string id, CancellationToken ct)
    {
        // Refuse excess previews rather than filling every management slot with waiting downloads.
        if (!await favoriteDownloads.WaitAsync(0, ct).ConfigureAwait(false))
            throw new TimeoutException("收藏预览繁忙，请稍后重试。");
        try { return await QqEmojis().ReadAsync(id, ct).ConfigureAwait(false); }
        finally { favoriteDownloads.Release(); }
    }
    private QqEmojiClient QqEmojis()
    {
        lock (qqEmojiGate)
        {
            if (qqEmojis is not null) return qqEmojis;
            if (qqEmojiFactory is not null) return qqEmojis = qqEmojiFactory();
            GroupChatSettings settings;
            lock (settingsGate) settings = GroupChatSettings.Load(Path.Combine(project, "group-chat.local.json"));
            var secret = BotSettings.LoadSecret(project, "ONEBOT_ACCESS_TOKEN");
            if (settings.SelfId <= 0 || string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("请先配置固定QQ账号与本机接口鉴权。");
            return qqEmojis = new QqEmojiClient(settings.OneBotBaseUrl, secret, settings.SelfId);
        }
    }
    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("面板本地路径不允许符号链接。");
    }
    private static long UserId(string? text) => long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new ArgumentException();
    private static string[] Tags(JsonElement body) => body.GetProperty("tags").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
    private static T Deserialize<T>(JsonElement body) => JsonSerializer.Deserialize<T>(body, BridgeConfig.JsonOptions) ?? throw new JsonException();
    private static async Task<JsonDocument> ReadJsonAsync(HttpListenerRequest request, CancellationToken ct, int maximumBytes = MaximumBodyBytes)
    {
        if (request.ContentLength64 > maximumBytes || request.ContentType?.Split(';')[0].Trim() != "application/json") throw new ArgumentException();
        using var data = new MemoryStream(); var buffer = new byte[8192];
        int read;
        while ((read = await request.InputStream.ReadAsync(buffer, ct)) > 0)
        { if (data.Length + read > maximumBytes) throw new ArgumentException(); data.Write(buffer, 0, read); }
        return JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }
    private static async Task JsonAsync(HttpListenerResponse response, object value, int code, CancellationToken ct)
    {
        response.StatusCode = code; response.ContentType = "application/json; charset=utf-8";
        await response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value, BridgeConfig.JsonOptions), ct);
    }
    private static async Task ErrorAsync(HttpListenerResponse response, string message, int code)
    { try { await JsonAsync(response, new { error = message }, code, CancellationToken.None); } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException) { } }
}
