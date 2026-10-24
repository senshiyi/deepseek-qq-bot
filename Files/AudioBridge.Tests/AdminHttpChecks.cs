using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AudioBridge;

public static class AdminHttpChecks
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    public static async Task<int> RunAsync()
    {
        var checks = new (string, Func<Task>)[]
        {
            ("loopback resources, bearer authentication, origin checks and CSP", Auth),
            ("HTTP persona, isolated notes, public preferences, history and meme CRUD", Data),
            ("history-to-persona generation requires review and rejects stale confirmation", PersonaLearningApi),
            ("companion mood, diary and wake settings round-trip through the panel API", CompanionApi),
            ("batch meme HTTP preserves partial successes and bounds the whole batch", MemeBatchApi),
            ("meme DELETE authenticates, preserves originals and immediately removes resolution", MemeDeletionApi),
            ("explicit private memory suggestions stay isolated and require an idempotent manual save", MemorySuggestionApi),
            ("QQ native faces and selected favorites import through authenticated HTTP", QqEmojis),
            ("received image previews and imports require current source approval and explicit global reuse approval", ReceivedImages),
            ("slow favorite previews stay bounded without delaying status or controls; stop cancels and releases", ConcurrentFavoritePreviews),
            ("sixteen pending handlers reject excess work and release cancelled controls", HandlerCapacity),
            ("settings validation, backup, saved/applied state and control allowlist", Settings),
            ("host apply replaces event owner; stop releases both ports and session lock", Host),
            ("legacy selfId zero resolves login and keeps chat running with received image import disabled", LegacyAccountHost)
        };
        foreach (var (name, run) in checks) { await run(); Console.WriteLine("PASS admin HTTP: " + name); }
        return checks.Length;
    }

    private static async Task Auth()
    {
        await using var fixture = new Fixture();
        using var unauthenticated = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(fixture.Server.Prefix) };
        using var denied = await unauthenticated.GetAsync("api/status");
        Check(denied.StatusCode == HttpStatusCode.Unauthorized, "API allowed missing bearer");
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/status");
        request.Headers.Add("Authorization", "Bearer " + Token); request.Headers.Add("Origin", "https://unrelated.invalid");
        using var wrongOrigin = await fixture.Http.SendAsync(request);
        Check(wrongOrigin.StatusCode == HttpStatusCode.Forbidden, "cross-origin API accepted");
        foreach (var file in new[] { "", "app.js", "companion.js", "app.css" })
        {
            using var resource = await unauthenticated.GetAsync(file);
            Check(resource.IsSuccessStatusCode && resource.Headers.Contains("Content-Security-Policy") &&
                (await resource.Content.ReadAsByteArrayAsync()).Length > 100, "embedded resource or CSP missing");
        }
        var status = (await fixture.Get("api/status")).GetRawText();
        Check(!status.Contains(Token, StringComparison.Ordinal) && !status.Contains("fixture-api-key", StringComparison.Ordinal), "status leaked a credential");
        using var missing = await fixture.Http.GetAsync("not-a-file");
        Check(missing.StatusCode == HttpStatusCode.NotFound, "unknown resource accepted");
    }

    private static async Task PersonaLearningApi()
    {
        await using var fixture = new Fixture(personaReplyFactory: _ => new FixedReply(
            "{\"name\":\"测试伙伴\",\"personality\":\"开朗耐心\",\"userAddress\":\"同学\",\"speakingStyle\":\"自然中文\"}"));
        var profiles = new BotProfileStore(fixture.Root);
        var before = profiles.GetPersona();
        new ContactContextStore(fixture.Root).AppendMessage(20, "user", "我希望你开朗耐心地聊天", "learning-fixture");
        using var noAuth = new HttpClient { BaseAddress = fixture.Http.BaseAddress };
        using var denied = await noAuth.PostAsJsonAsync("api/persona-learning/generate", new { userId = 20 });
        using var invalid = await fixture.Request("POST", "api/persona-learning/generate", new { userId = 30 });
        Check(denied.StatusCode == HttpStatusCode.Unauthorized && invalid.StatusCode == HttpStatusCode.BadRequest,
            "persona learning bypassed auth or private allowlist");
        var generated = await fixture.Send("POST", "api/persona-learning/generate", new { userId = 20 });
        Check(profiles.GetPersona() == before && generated.GetProperty("draft").GetProperty("sourceCount").GetInt32() == 1,
            "generation saved a persona or lost source metadata");
        var id = generated.GetProperty("draftId").GetString();
        var confirmed = await fixture.Send("POST", "api/persona-learning/confirm", new
        { draftId = id, name = "审阅后的名字", personality = "开朗耐心", userAddress = "同学", speakingStyle = "自然中文" });
        Check(confirmed.GetProperty("name").GetString() == "审阅后的名字" &&
            profiles.GetPersona().ReplyMaxCharacters == before.ReplyMaxCharacters &&
            (await fixture.Get("api/status")).GetProperty("restartRequired").GetBoolean(),
            "confirmation did not save reviewed fields or mutated unrelated settings");
        using var twice = await fixture.Request("POST", "api/persona-learning/confirm", new
        { draftId = id, name = "重复", personality = "开朗", userAddress = "你", speakingStyle = "自然" });
        Check(twice.StatusCode == HttpStatusCode.BadRequest, "consumed draft could be confirmed again");
        var second = await fixture.Send("POST", "api/persona-learning/generate", new { userId = 20 });
        profiles.SavePersona(profiles.GetPersona() with { Name = "手动优先" });
        using var stale = await fixture.Request("POST", "api/persona-learning/confirm", new
        { draftId = second.GetProperty("draftId").GetString(), name = "过期覆盖", personality = "开朗", userAddress = "你", speakingStyle = "自然" });
        Check(stale.StatusCode == HttpStatusCode.Conflict && profiles.GetPersona().Name == "手动优先",
            "stale learning draft overwrote a newer manual persona");
        var noopDraft = await fixture.Send("POST", "api/persona-learning/generate", new { userId = 20 });
        var current = profiles.GetPersona();
        var confirmations = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Request("POST", "api/persona-learning/confirm", new
        {
            draftId = noopDraft.GetProperty("draftId").GetString(), name = current.Name,
            personality = current.Personality, userAddress = current.UserAddress, speakingStyle = current.SpeakingStyle
        })));
        try
        {
            Check(confirmations.Count(response => response.StatusCode == HttpStatusCode.OK) == 1 &&
                confirmations.Count(response => response.StatusCode == HttpStatusCode.BadRequest) == 7 && profiles.GetPersona() == current,
                "same no-op draft was consumed more than once by concurrent confirmations");
        }
        finally { foreach (var response in confirmations) response.Dispose(); }
    }

    private static async Task CompanionApi()
    {
        await using var fixture = new Fixture();
        var store = new CompanionStore(fixture.Root);
        var entry = store.AddEntry(20, DiaryLevels.Important, "他今天面试", "担心", "希望顺利");
        store.SetMood(20, "有点想他");
        var listed = await fixture.Get("api/companion");
        var contact = listed.GetProperty("contacts")[0];
        Check(contact.GetProperty("mood").GetString() == "有点想他" && contact.GetProperty("entries").GetArrayLength() == 1,
            "companion API hid the saved mood or diary");
        Check(contact.GetProperty("entries")[0].GetProperty("levelLabel").GetString() == "重要" &&
            contact.GetProperty("entries")[0].GetProperty("id").GetString() == entry.Id,
            "companion API lost a diary level label or identifier");
        Check(!listed.GetProperty("settings").GetProperty("enabled").GetBoolean(), "companion defaults enabled the wake loop");
        var saved = await fixture.Send("PUT", "api/companion-settings",
            new { enabled = true, auditHour = 5, idleMinutes = 90, cooldownMinutes = 60, dailyLimit = 3 });
        Check(saved.GetProperty("auditHour").GetInt32() == 5 && store.LoadSettings().Enabled && store.LoadSettings().DailyLimit == 3,
            "companion settings were not saved through the panel API");
        using var invalid = await fixture.Request("PUT", "api/companion-settings",
            new { enabled = true, auditHour = 30, idleMinutes = 90, cooldownMinutes = 60, dailyLimit = 3 });
        Check(invalid.StatusCode == HttpStatusCode.BadRequest && store.LoadSettings().AuditHour == 5,
            "companion API accepted an invalid hour or left a partial write");
        using var removed = await fixture.Request("DELETE", $"api/companion/20/entries/{entry.Id}");
        Check(removed.StatusCode == HttpStatusCode.OK && store.Get(20).Entries.Count == 0,
            "companion API did not delete the diary entry");
        using var unknown = await fixture.Request("DELETE", "api/companion/20/entries/" + Guid.NewGuid().ToString("D"));
        Check(unknown.StatusCode == HttpStatusCode.OK, "companion API failed on an already absent entry");
    }

    private static async Task MemeBatchApi()
    {
        await using var fixture = new Fixture();
        var encoded = Convert.ToBase64String(MemeLibraryChecks.Png);
        var result = await fixture.Send("POST", "api/memes/batch", new { items = new[] {
            new { clientId = "a", id = "batch_one", tags = new[] { "合成" }, base64 = encoded },
            new { clientId = "b", id = "batch_one", tags = new[] { "重复" }, base64 = encoded },
            new { clientId = "c", id = "batch_two", tags = Array.Empty<string>(), base64 = encoded }
        } });
        Check(result.GetProperty("importedCount").GetInt32() == 2 && result.GetProperty("failedCount").GetInt32() == 1 &&
            new MemeLibrary(fixture.Root).Get("batch_one").Tags.SequenceEqual(["合成"]) &&
            !(await fixture.Get("api/status")).GetProperty("restartRequired").GetBoolean(),
            "batch import hid partial failure, overwrote bytes/tags, or required reload");
        using var tooMany = await fixture.Request("POST", "api/memes/batch", new { items = Enumerable.Range(0, 21)
            .Select(i => new { clientId = "n" + i, id = "large_" + i, tags = Array.Empty<string>(), base64 = encoded }) });
        Check(tooMany.StatusCode == HttpStatusCode.BadRequest && new MemeLibrary(fixture.Root).List().Count == 2,
            "oversized batch wrote partial data before preflight");
    }

    private static async Task Data()
    {
        await using var fixture = new Fixture();
        var persona = new PersonaSettings { Personality = "PRIVATE_RELATION_测试", UserAddress = "联系人", ReplyMaxCharacters = 240 };
        await fixture.Send("PUT", "api/persona", persona);
        Check((await fixture.Get("api/persona")).GetProperty("replyMaxCharacters").GetInt32() == 240, "persona not saved");
        var memory = await fixture.Send("POST", "api/memories", new { userId = 20, text = "PRIVATE_SENTINEL" });
        var id = memory.GetProperty("id").GetString();
        Check((await fixture.Get("api/memories?userId=21")).GetArrayLength() == 0, "notes crossed contacts");
        await fixture.Send("PUT", "api/memories/" + id, new { userId = 20, text = "PRIVATE_EDITED" });
        Check(new BotProfileStore(fixture.Root).BuildPrivateMemoryContext(20).Contains("PRIVATE_EDITED", StringComparison.Ordinal), "note edit lost");
        await fixture.Send("PUT", "api/public-preferences", new PublicContactPreference(20, "同学", "friendly", "short", true));
        var contacts = new ContactContextStore(fixture.Root);
        var publicContext = contacts.BuildGroupPreferenceContext(20);
        Check(publicContext.Contains(JsonSerializer.Serialize("同学"), StringComparison.Ordinal) && !publicContext.Contains("PRIVATE_", StringComparison.Ordinal), "group memory included private content");
        contacts.AppendMessage(20, "user", "PRIVATE_RAW", "1");
        Check((await fixture.Get("api/history?userId=20")).GetArrayLength() == 1, "history not listed");
        await fixture.Send("DELETE", "api/history?userId=20");
        Check(contacts.Recent(20).Count == 0 && new BotProfileStore(fixture.Root).ListMemories(20).Count == 1, "raw deletion removed long notes");
        await fixture.Send("DELETE", "api/memories/" + id + "?userId=20");
        Check((await fixture.Get("api/memories?userId=20")).GetArrayLength() == 0, "note not removed");
        var image = MemeLibraryChecks.Png;
        await fixture.Send("POST", "api/memes", new { id = "fixture", tags = new[] { "测试" }, base64 = Convert.ToBase64String(image) });
        await fixture.Send("PUT", "api/memes/fixture", new { tags = new[] { "收到" } });
        using var preview = await fixture.Http.GetAsync("api/memes/fixture/image");
        Check(preview.IsSuccessStatusCode && (await preview.Content.ReadAsByteArrayAsync()).SequenceEqual(image), "uploaded image preview altered bytes");
        Check((await fixture.Get("api/memes")).GetArrayLength() == 1 && new MemeLibrary(fixture.Root).List().Single().Tags.Contains("收到"), "meme tags/list not updated");
    }

    private static async Task MemeDeletionApi()
    {
        await using var fixture = new Fixture();
        var library = new MemeLibrary(fixture.Root);
        library.ImportBytes("remove", MemeLibraryChecks.Png, ["待删除"]);
        library.ImportBytes("keep", MemeLibraryChecks.Png, ["保留"]);
        library.AddFace(14, ["原生"]);
        using var unauthenticated = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = fixture.Http.BaseAddress };
        using var denied = await unauthenticated.DeleteAsync("api/memes/remove");
        Check(denied.StatusCode == HttpStatusCode.Unauthorized && library.List().Count == 3,
            "unauthenticated delete changed the library");
        using var originRequest = new HttpRequestMessage(HttpMethod.Delete, "api/memes/remove");
        originRequest.Headers.Add("Origin", "https://unrelated.invalid");
        using var wrongOrigin = await fixture.Http.SendAsync(originRequest);
        using var invalid = await fixture.Request("DELETE", "api/memes/UPPER");
        Check(wrongOrigin.StatusCode == HttpStatusCode.Forbidden && invalid.StatusCode == HttpStatusCode.BadRequest && library.List().Count == 3,
            "cross-origin or invalid-id deletion changed the library");
        var deleted = await fixture.Send("DELETE", "api/memes/remove");
        Check(deleted.GetProperty("deleted").GetBoolean() && deleted.GetProperty("originalFileRetained").GetBoolean(),
            "delete result omitted removal or retained-source state");
        await fixture.Send("DELETE", "api/memes/qq_face_14");
        var repeated = await fixture.Send("DELETE", "api/memes/remove");
        using var removedPreview = await fixture.Http.GetAsync("api/memes/remove/image");
        using var removedEdit = await fixture.Request("PUT", "api/memes/remove", new { tags = new[] { "恢复" } });
        Check(!repeated.GetProperty("deleted").GetBoolean() && removedPreview.StatusCode == HttpStatusCode.BadRequest &&
              removedEdit.StatusCode == HttpStatusCode.BadRequest &&
              (await fixture.Get("api/memes")).GetArrayLength() == 1 && library.List().Single().Id == "keep" &&
              File.ReadAllBytes(Path.Combine(fixture.Root, "Files/Memes/remove.png")).SequenceEqual(MemeLibraryChecks.Png) &&
              library.Get("keep").Content.SequenceEqual(MemeLibraryChecks.Png),
            "delete did not persist, left active access or altered unrelated/source bytes");
        Check(!(await fixture.Get("api/status")).GetProperty("restartRequired").GetBoolean(), "meme edits incorrectly require restarting the message session");
    }

    private static async Task MemorySuggestionApi()
    {
        await using var fixture = new Fixture();
        var contacts = new ContactContextStore(fixture.Root);
        contacts.AppendMessage(20, "user", "请记住：喜欢热茶", "explicit");
        contacts.AppendMessage(20, "user", "普通闲聊，不要求记忆", "ordinary");
        contacts.AppendMessage(20, "assistant", "记住：助手自述不能作依据", "assistant");
        contacts.AppendMessage(20, "user", "记住：过期请求", "expired", DateTimeOffset.UtcNow.AddDays(-8));
        contacts.AppendMessage(30, "user", "记住：另一位联系人的偏好", "other-contact");
        var candidates = await fixture.Get("api/memory-suggestions?userId=20");
        Check(candidates.GetArrayLength() == 1 && candidates[0].GetProperty("text").GetString() == "喜欢热茶" &&
            candidates[0].GetProperty("sourceMessageId").GetString() == "explicit" &&
            candidates[0].GetProperty("userId").GetInt64() == 20, "suggestion source, retention or contact boundary changed");
        Check((await fixture.Get("api/memory-suggestions?userId=21")).GetArrayLength() == 0 &&
            (await fixture.Get("api/memories?userId=20")).GetArrayLength() == 0 &&
            contacts.BuildGroupPreferenceContext(20) == "", "suggestion saved itself or became group-visible");
        using var unauthenticated = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = fixture.Http.BaseAddress };
        using var denied = await unauthenticated.GetAsync("api/memory-suggestions?userId=20");
        using var invalid = await fixture.Http.GetAsync("api/memory-suggestions?userId=0");
        Check(denied.StatusCode == HttpStatusCode.Unauthorized && invalid.StatusCode == HttpStatusCode.BadRequest,
            "suggestions endpoint bypassed authentication or identifier validation");
        var first = await fixture.Send("POST", "api/memories", new { userId = 20, text = candidates[0].GetProperty("text").GetString() });
        var repeated = await fixture.Send("POST", "api/memories", new { userId = 20, text = "喜欢热茶" });
        Check(first.GetProperty("id").GetString() == repeated.GetProperty("id").GetString() &&
            (await fixture.Get("api/memories?userId=20")).GetArrayLength() == 1 &&
            (await fixture.Get("api/memory-suggestions?userId=20")).GetArrayLength() == 0, "manual approval was duplicated or stayed pending");
        contacts.AppendMessage(20, "user", "记住：尚未保存的第二条", "next");
        Check((await fixture.Get("api/memory-suggestions?userId=20")).GetArrayLength() == 1, "fresh request was not listed");
        await fixture.Send("DELETE", "api/history?userId=20");
        Check((await fixture.Get("api/memory-suggestions?userId=20")).GetArrayLength() == 0 &&
            (await fixture.Get("api/memories?userId=20")).GetArrayLength() == 1, "history deletion retained a candidate or removed approved notes");
    }

    private static async Task ReceivedImages()
    {
        var images = new FavoriteImage();
        await using var fixture = new Fixture(favoriteImage: images);
        var groupId = fixture.AddReceived(messageId: 101);
        var privateId = fixture.AddReceived(isPrivate: true, messageId: 102);
        var candidates = await fixture.Get("api/received-images");
        Check(candidates.GetArrayLength() == 2 && !candidates.GetRawText().Contains("qpic", StringComparison.OrdinalIgnoreCase) &&
            candidates.EnumerateArray().Any(item => item.GetProperty("isPrivate").GetBoolean() && item.GetProperty("groupId").GetInt64() == 0),
            "received list exposed source URL or lost its private/group label");
        using var preview = await fixture.Http.GetAsync("api/received-images/" + privateId + "/image");
        Check(preview.IsSuccessStatusCode && (await preview.Content.ReadAsByteArrayAsync()).SequenceEqual(MemeLibraryChecks.Png) &&
            new MemeLibrary(fixture.Root).List().Count == 0, "preview changed bytes or imported an unapproved received image");
        foreach (var approval in new object?[] { null, false, "true", 1 })
        {
            var body = new Dictionary<string, object?> { ["receivedId"] = groupId, ["id"] = "unapproved", ["tags"] = new[] { "测试" } };
            if (approval is not null) body["reuseApproved"] = approval;
            using var denied = await fixture.Request("POST", "api/received-images/import", body);
            Check(denied.StatusCode == HttpStatusCode.BadRequest && images.Calls == 1 && new MemeLibrary(fixture.Root).List().Count == 0,
                "missing, false or malformed global reuse approval downloaded or saved an image");
        }
        using var arbitrary = await fixture.Request("POST", "api/received-images/import", new
            { receivedId = "https://p.qpic.cn/arbitrary.png", id = "arbitrary", tags = Array.Empty<string>(), reuseApproved = true });
        Check(arbitrary.StatusCode == HttpStatusCode.BadRequest && images.Calls == 1, "arbitrary received URL reached HTTP");
        await fixture.Send("POST", "api/received-images/import", new { receivedId = groupId, id = "received", tags = new[] { "收到" }, reuseApproved = true });
        Check(new MemeLibrary(fixture.Root).Get("received").Content.SequenceEqual(MemeLibraryChecks.Png) &&
            !(await fixture.Get("api/status")).GetProperty("restartRequired").GetBoolean(), "approved import was not stored or incorrectly required a restart");
        var original = GroupChatSettings.Load(Path.Combine(fixture.Root, "group-chat.local.json"));
        await fixture.Send("PUT", "api/settings", original with { AllowedPrivateUserIds = [], AllowedGroupIds = [10, 11], ActiveGroupId = 11 });
        Check((await fixture.Get("api/received-images")).GetArrayLength() == 0, "saved allowlist or active-group change left candidates visible");
        var calls = images.Calls;
        foreach (var id in new[] { groupId, privateId })
        {
            using var deniedPreview = await fixture.Http.GetAsync("api/received-images/" + id + "/image");
            using var deniedImport = await fixture.Request("POST", "api/received-images/import", new
                { receivedId = id, id = "revoked", tags = Array.Empty<string>(), reuseApproved = true });
            Check(deniedPreview.StatusCode == HttpStatusCode.BadRequest && deniedImport.StatusCode == HttpStatusCode.BadRequest && images.Calls == calls,
                "revoked source downloaded or imported an image");
        }
        await fixture.Send("PUT", "api/settings", original);
        fixture.ImageClock.Now = fixture.ImageClock.Now.AddMinutes(10);
        Check((await fixture.Get("api/received-images")).GetArrayLength() == 0, "expired received image remained listed");
        using var expired = await fixture.Http.GetAsync("api/received-images/" + groupId + "/image");
        Check(expired.StatusCode == HttpStatusCode.BadRequest && images.Calls == calls, "expired candidate downloaded bytes");
        var lateId = fixture.AddReceived(isPrivate: true, messageId: 103);
        images.After = () => File.WriteAllText(Path.Combine(fixture.Root, "group-chat.local.json"),
            JsonSerializer.Serialize(original with { AllowedPrivateUserIds = [] }, BridgeConfig.JsonOptions));
        using var late = await fixture.Request("POST", "api/received-images/import", new
            { receivedId = lateId, id = "late", tags = Array.Empty<string>(), reuseApproved = true });
        Check(late.StatusCode == HttpStatusCode.BadRequest && new MemeLibrary(fixture.Root).List().Single().Id == "received",
            "source revoked during download persisted late bytes");
        images.After = null;
        await fixture.Send("PUT", "api/settings", original);
        var expiringId = fixture.AddReceived(messageId: 104);
        images.After = () => fixture.ImageClock.Now = fixture.ImageClock.Now.AddMinutes(10);
        using var lateExpired = await fixture.Request("POST", "api/received-images/import", new
            { receivedId = expiringId, id = "late-expired", tags = Array.Empty<string>(), reuseApproved = true });
        Check(lateExpired.StatusCode == HttpStatusCode.BadRequest && new MemeLibrary(fixture.Root).List().Single().Id == "received",
            "candidate expired during download persisted late bytes");
    }

    private static async Task Settings()
    {
        await using var fixture = new Fixture();
        var original = GroupChatSettings.Load(Path.Combine(fixture.Root, "group-chat.local.json"));
        await fixture.Send("PUT", "api/settings", original with { PrivateIntervalSeconds = 3, PrivatePerHour = 60,
            MentionIntervalSeconds = 3, MentionPerHour = 60, ActiveIntervalSeconds = 3, ActivePerHour = 120, ActiveGroupId = 10 });
        var saved = await fixture.Get("api/settings");
        Check(saved.GetProperty("privateIntervalSeconds").GetInt32() == 3 && saved.GetProperty("privatePerHour").GetInt32() == 60 &&
            saved.GetProperty("activePerHour").GetInt32() == 120, "relaxed rate settings not persisted");
        Check((await fixture.Get("api/status")).GetProperty("restartRequired").GetBoolean(), "saved change not marked pending");
        Check(Directory.GetFiles(Path.Combine(fixture.Root, "Results/local"), "group-chat.panel-before-*.json").Length == 1, "settings backup missing");
        using var invalid = await fixture.Request("PUT", "api/settings", original with { SelfId = 100 });
        Check(invalid.StatusCode == HttpStatusCode.BadRequest && GroupChatSettings.Load(Path.Combine(fixture.Root, "group-chat.local.json")).SelfId == 99, "fixed account mutated");
        foreach (var action in new[] { "pause", "resume", "apply" }) await fixture.Send("POST", "api/control", new { action });
        Check(fixture.Actions.SequenceEqual(new[] { "pause", "resume", "apply" }) && !(await fixture.Get("api/status")).GetProperty("restartRequired").GetBoolean(), "controls or applied state wrong");
        using var command = await fixture.Request("POST", "api/control", new { action = "run-command" });
        Check(command.StatusCode == HttpStatusCode.BadRequest, "arbitrary management action accepted");
        using var broken = await fixture.Http.PostAsync("api/memories", new StringContent("{", Encoding.UTF8, "application/json"));
        Check(broken.StatusCode == HttpStatusCode.BadRequest && (await fixture.Get("api/status")).ValueKind == JsonValueKind.Object, "bad JSON killed server");
    }

    private static async Task QqEmojis()
    {
        await using var fixture = new Fixture();
        await fixture.Send("POST", "api/qq-faces", new { faceId = 14, tags = new[] { "惊讶" } });
        var item = (await fixture.Get("api/memes"))[0];
        Check(item.GetProperty("faceId").GetInt32() == 14 && item.GetProperty("previewUrl").ValueKind == JsonValueKind.Null,
            "native face listed as a downloadable image");
        Check(new MemeLibrary(fixture.Root).ResolveSegment("qq_face_14").Type == "face", "native face was not registered");
        using var duplicate = await fixture.Request("POST", "api/qq-faces", new { faceId = 14, tags = new[] { "覆盖" } });
        Check(duplicate.StatusCode == HttpStatusCode.BadRequest, "duplicate native face overwritten");
        var favorites = await fixture.Get("api/qq-favorites");
        Check(favorites.GetArrayLength() == 1 && !favorites.GetRawText().Contains("qpic.cn", StringComparison.Ordinal), "favorite API exposed URLs");
        var id = favorites[0].GetProperty("id").GetString();
        using var preview = await fixture.Http.GetAsync("api/qq-favorites/" + id + "/image");
        Check(preview.IsSuccessStatusCode && (await preview.Content.ReadAsByteArrayAsync()).SequenceEqual(MemeLibraryChecks.Png), "favorite preview failed");
        Check(new MemeLibrary(fixture.Root).List().Count == 1, "preview imported an unselected favorite");
        await fixture.Send("POST", "api/qq-favorites/import", new { favoriteId = id, id = "qq_favorite_test", tags = new[] { "开心" } });
        Check(new MemeLibrary(fixture.Root).Get("qq_favorite_test").Content.SequenceEqual(MemeLibraryChecks.Png), "selected favorite bytes not stored");
        using var arbitrary = await fixture.Request("POST", "api/qq-favorites/import", new { favoriteId = "https://evil.invalid/a.png", id = "arbitrary", tags = Array.Empty<string>() });
        Check(arbitrary.StatusCode == HttpStatusCode.BadRequest && new MemeLibrary(fixture.Root).List().Count == 2, "arbitrary favorite source accepted");
    }

    private static Task Host() => HostWithAccount(false);
    private static Task LegacyAccountHost() => HostWithAccount(true);

    private static async Task HostWithAccount(bool resolveAccount)
    {
        await using var fixture = new Fixture(start: false);
        using var loginApi = new HttpListener();
        loginApi.Prefixes.Add($"http://127.0.0.1:{FreePort()}/"); loginApi.Start();
        var loginCalls = 0;
        var loginWork = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var context = await loginApi.GetContextAsync();
                    Check(context.Request.Url!.AbsolutePath == "/get_login_info" &&
                        context.Request.Headers["Authorization"] == "Bearer fixture-onebot", "host called an unexpected OneBot endpoint");
                    Interlocked.Increment(ref loginCalls);
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { status = "ok", retcode = 0, data = new { user_id = 99 } }));
                    context.Response.Close();
                }
            }
            catch (Exception error) when (error is ObjectDisposedException or HttpListenerException) { }
        });
        var originalDirectory = Environment.CurrentDirectory;
        var previousToken = Environment.GetEnvironmentVariable("ONEBOT_ACCESS_TOKEN");
        var previousKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        using var stopping = new CancellationTokenSource();
        Task? work = null;
        try
        {
            Environment.CurrentDirectory = fixture.Root;
            Environment.SetEnvironmentVariable("ONEBOT_ACCESS_TOKEN", "fixture-onebot");
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "fixture-api-key");
            Console.SetOut(output);
            var settings = GroupChatSettings.Load(Path.Combine(fixture.Root, "group-chat.local.json")) with
                { SelfId = resolveAccount ? 0 : 99, OneBotBaseUrl = loginApi.Prefixes.Single() };
            File.WriteAllText(Path.Combine(fixture.Root, "group-chat.local.json"), JsonSerializer.Serialize(settings, BridgeConfig.JsonOptions));
            work = ChatApplicationHost.RunAsync(settings, "10", 0, false, "fixed", stopping.Token, fixture.Port);
            var actualToken = LocalAdminServer.LoadOrCreateToken(fixture.Root);
            fixture.Http.DefaultRequestHeaders.Authorization = new("Bearer", actualToken);
            await WaitFor(async () => (await fixture.Get("api/status")).GetProperty("runtime").GetProperty("running").GetBoolean());
            Check((await fixture.Get("api/received-images")).GetArrayLength() == 0, "fresh host listed unreceived images");
            if (resolveAccount)
            {
                using var missing = await fixture.Http.GetAsync("api/received-images/not-selected/image");
                Check(missing.StatusCode == HttpStatusCode.BadRequest && loginCalls == 1,
                    "legacy host enabled unbound image reads or failed to resolve its account");
            }
            await fixture.Send("PUT", "api/settings", settings with { AllowedGroupIds = [10, 11], ActiveGroupId = 11 });
            await fixture.Send("POST", "api/control", new { action = "apply" });
            await WaitFor(async () =>
            {
                var runtime = (await fixture.Get("api/status")).GetProperty("runtime");
                return runtime.TryGetProperty("groupId", out var group) && group.GetInt64() == 11 && runtime.GetProperty("running").GetBoolean();
            });
            await fixture.Send("POST", "api/control", new { action = "stop" });
            await work.WaitAsync(TimeSpan.FromSeconds(3));
            using var listener = new HttpListener(); listener.Prefixes.Add(fixture.Server.Prefix);
            listener.Prefixes.Add($"http://127.0.0.1:{settings.EventPort}/onebot/"); listener.Start(); listener.Close();
            using var sessionLock = new FileStream(Path.Combine(fixture.Root, "Results/local/group-chat-session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Check(!File.Exists(Path.Combine(fixture.Root, "Results/local/deepseek-budget.json")), "host lifecycle test made paid request");
            Check(loginCalls == (resolveAccount ? 2 : 0), "host account resolution was skipped or unexpectedly repeated");
        }
        finally
        {
            stopping.Cancel();
            try { if (work is not null) await work.WaitAsync(TimeSpan.FromSeconds(3)); }
            finally
            {
                Environment.CurrentDirectory = originalDirectory; Console.SetOut(originalOutput);
                Environment.SetEnvironmentVariable("ONEBOT_ACCESS_TOKEN", previousToken);
                Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", previousKey);
                loginApi.Close(); await loginWork;
            }
        }
    }

    private static async Task ConcurrentFavoritePreviews()
    {
        var images = new BlockedFavoriteImage();
        await using var fixture = new Fixture(favoriteImage: images);
        var lists = await Task.WhenAll(fixture.Get("api/qq-favorites"), fixture.Get("api/qq-favorites"));
        Check(fixture.FavoriteClientCreations == 1, "concurrent favorite requests created competing QQ clients");
        var id = lists[0][0].GetProperty("id").GetString();
        var path = "api/qq-favorites/" + id + "/image";
        var first = fixture.Http.GetAsync(path);
        await images.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = fixture.Http.GetAsync(path);
        await images.TwoEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            using var excess = await fixture.Http.GetAsync(path).WaitAsync(TimeSpan.FromSeconds(1));
            Check(excess.StatusCode == HttpStatusCode.ServiceUnavailable && images.Active == 2 && images.MaximumActive == 2,
                "excess favorite preview queued or exceeded two downloads");
            var status = await fixture.Get("api/status").WaitAsync(TimeSpan.FromSeconds(1));
            Check(status.GetProperty("runtime").GetProperty("running").GetBoolean(), "slow preview blocked status");
            await fixture.Send("POST", "api/control", new { action = "pause" }).WaitAsync(TimeSpan.FromSeconds(1));
            await fixture.Send("POST", "api/control", new { action = "stop" }).WaitAsync(TimeSpan.FromSeconds(1));
            Check(fixture.Actions.SequenceEqual(new[] { "pause", "stop" }), "slow previews delayed or reordered controls");
        }
        finally
        {
            await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await ObserveClosedRequest(first); await ObserveClosedRequest(second);
        }
        Check(images.Cancelled == 2 && images.Active == 0 && images.Disposed,
            "server stop did not cancel downloads before disposing the QQ client");
        using var listener = new HttpListener(); listener.Prefixes.Add(fixture.Server.Prefix); listener.Start(); listener.Close();
    }

    private static async Task HandlerCapacity()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(onControl: _ => { entered.TrySetResult(); return release.Task; });
        var pending = Enumerable.Range(0, 24).Select(_ => fixture.Request("POST", "api/control", new { action = "pause" })).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            // Wait for the control burst itself to overflow. A status probe sent before all
            // controls arrive can steal slot 16, reject a control, and leave only 15 blocked.
            using var refused = await await Task.WhenAny(pending).WaitAsync(TimeSpan.FromSeconds(1));
            Check(refused.StatusCode == HttpStatusCode.ServiceUnavailable && refused.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(1),
                "control overflow did not advertise a bounded retry");
            using var extra = await fixture.Http.GetAsync("api/status");
            Check(extra.StatusCode == HttpStatusCode.ServiceUnavailable && fixture.Actions.Count == 1,
                "handler capacity did not reject excess work or controls ran concurrently");
        }
        finally
        {
            var stopped = fixture.StopAsync();
            release.TrySetResult();
            await stopped.WaitAsync(TimeSpan.FromSeconds(2));
            foreach (var request in pending) await ObserveClosedRequest(request);
        }
        Check(fixture.Actions.Count == 1, "cancelled queued control executed during shutdown");
    }

    private static async Task ObserveClosedRequest(Task<HttpResponseMessage> request)
    {
        try { using var response = await request.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
    }

    public static async Task PreviewAsync()
    {
        await using var fixture = new Fixture(port: 17812, personaReplyFactory: _ => new FixedReply(
            "{\"name\":\"合成伙伴\",\"personality\":\"开朗耐心\",\"userAddress\":\"同学\",\"speakingStyle\":\"自然中文\"}"));
        new BotProfileStore(fixture.Root).AddMemory(20, "这是隔离的浏览器测试笔记。");
        new ContactContextStore(fixture.Root).AppendMessage(20, "user", "合成私聊原文，不来自真实QQ。", "preview-1");
        new ContactContextStore(fixture.Root).AppendMessage(20, "user", "请记住：合成联系人喜欢简短回答。", "preview-memory-request");
        fixture.AddReceived(messageId: 1001);
        fixture.AddReceived(isPrivate: true, messageId: 1002);
        Console.WriteLine("Isolated admin browser fixture: http://127.0.0.1:17812/ ; test token is fixture constant; no QQ/model connected.");
        Console.WriteLine("Fixture directory: " + fixture.Root);
        await Task.Delay(TimeSpan.FromMinutes(20));
    }

    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < end)
        {
            try { if (await condition()) return; } catch (HttpRequestException) { }
            await Task.Delay(30);
        }
        throw new Exception("admin runtime state did not settle");
    }
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static int FreePort() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine("Cache/admin-http-checks", Guid.NewGuid().ToString("N")));
        public int Port { get; }
        public LocalAdminServer Server { get; }
        public HttpClient Http { get; }
        public ConcurrentQueue<string> Actions { get; } = new();
        public ImageTime ImageClock { get; } = new();
        public ReceivedImageInbox Received { get; }
        public int FavoriteClientCreations;
        private readonly CancellationTokenSource stopping = new();
        private readonly Task? work;
        public Fixture(bool start = true, int? port = null, HttpMessageHandler? favoriteImage = null, Func<string, Task>? onControl = null,
            Func<string, IStreamingReply>? personaReplyFactory = null)
        {
            Directory.CreateDirectory(Path.Combine(Root, "Files/Memes"));
            File.WriteAllText(Path.Combine(Root, "Files/Memes/manifest.json"), "{\"version\":1,\"items\":[]}");
            var settings = new GroupChatSettings { SelfId = 99, AllowedGroupIds = [10], AllowedPrivateUserIds = [20], EventPort = FreePort() };
            File.WriteAllText(Path.Combine(Root, "group-chat.local.json"), JsonSerializer.Serialize(settings, BridgeConfig.JsonOptions));
            Port = port ?? FreePort();
            Received = new ReceivedImageInbox(99, ImageClock);
            Server = new LocalAdminServer(Root, Token, () => new { running = true, paused = Actions.LastOrDefault() == "pause", engine = "school", model = "fixture-model", apiBaseUrl = "http://fixture.invalid/v1/", keyEnvironment = "SCHOOL_API_KEY", groupId = 10,
                budget = new { reservedPoints = .004, maxPoints = (decimal?)null, calls = 3, maxCalls = (int?)null } }, async action =>
                { Actions.Enqueue(action); if (onControl is not null) await onControl(action); if (action == "apply") Server!.Applied(); }, Port,
                () => { Interlocked.Increment(ref FavoriteClientCreations); return new QqEmojiClient("http://127.0.0.1:3000/", "fixture-onebot", 99, new FavoriteApi(), favoriteImage ?? new FavoriteImage()); }, Received,
                personaReplyFactory: personaReplyFactory);
            Http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(Server.Prefix), Timeout = TimeSpan.FromSeconds(2) };
            Http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
            if (start) work = Server.RunAsync(stopping.Token);
        }
        public Task<HttpResponseMessage> Request(string method, string path, object? body = null) => Http.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
            { Content = body is null ? null : new StringContent(JsonSerializer.Serialize(body, BridgeConfig.JsonOptions), Encoding.UTF8, "application/json") });
        public async Task<JsonElement> Send(string method, string path, object? body = null)
        {
            using var response = await Request(method, path, body);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new Exception($"admin fixture {method} {path}: {response.StatusCode}: {text}");
            using var json = JsonDocument.Parse(text); return json.RootElement.Clone();
        }
        public Task<JsonElement> Get(string path) => Send("GET", path);
        public string AddReceived(bool isPrivate = false, long messageId = 1)
        {
            var settings = GroupChatSettings.Load(Path.Combine(Root, "group-chat.local.json"));
            var policy = new GroupChatPolicy(new() { SelfId = settings.SelfId, AllowedGroupIds = settings.AllowedGroupIds,
                AllowedPrivateUserIds = settings.AllowedPrivateUserIds }, () => ImageClock.Now);
            using var message = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                post_type = "message", message_type = isPrivate ? "private" : "group", sub_type = "normal", self_id = 99,
                user_id = 20, group_id = 10, message_id = messageId, sender = new { user_id = 20 }, time = ImageClock.Now.ToUnixTimeSeconds(),
                message = new[] { new { type = "image", data = new { url = "https://p.qpic.cn/synthetic-received.png" } } }
            }));
            Check(policy.TryParse(message.RootElement, out var parsed, out _) && Received.Observe(message.RootElement, parsed!),
                "synthetic received fixture was not admitted");
            return Received.List().Single(item => item.MessageId == messageId).Id;
        }
        public async Task StopAsync() { stopping.Cancel(); if (work is not null) await work; }
        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Http.Dispose(); stopping.Dispose();
            var expected = Path.GetFullPath("Cache/admin-http-checks") + Path.DirectorySeparatorChar;
            if (!Root.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("test cleanup outside cache");
            // Cache cleanup only: a virus scanner or a just-closed handle can briefly lock a freshly written temp file.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(Root, true); return; }
                catch (IOException) when (attempt < 4) { await Task.Delay(100); }
                catch (IOException) { return; }
                catch (UnauthorizedAccessException) when (attempt < 4) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) { return; }
            }
        }
    }

    private sealed class FavoriteApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            object data = request.RequestUri!.AbsolutePath switch
            {
                "/get_login_info" => new { user_id = 99 },
                "/fetch_custom_face" => new[] { "https://p.qpic.cn/fixture-preview.png" },
                _ => throw new Exception("unexpected favorite API action")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { status = "ok", retcode = 0, data }), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class FavoriteImage : HttpMessageHandler
    {
        public int Calls;
        public Action? After;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            Check(request.Headers.Authorization is null && request.RequestUri!.Host == "p.qpic.cn", "favorite download credentials or target wrong");
            After?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MemeLibraryChecks.Png) });
        }
    }

    private sealed class ImageTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BlockedFavoriteImage : HttpMessageHandler
    {
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwoEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active;
        public int MaximumActive;
        public int Cancelled;
        public bool Disposed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(request.Headers.Authorization is null, "favorite preview leaked OneBot credentials");
            var active = Interlocked.Increment(ref Active);
            int previous;
            do { previous = Volatile.Read(ref MaximumActive); if (active <= previous) break; }
            while (Interlocked.CompareExchange(ref MaximumActive, active, previous) != previous);
            FirstEntered.TrySetResult(); if (active == 2) TwoEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new Exception("blocked favorite unexpectedly completed");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { Interlocked.Increment(ref Cancelled); throw; }
            finally { Interlocked.Decrement(ref Active); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Check(Active == 0, "QQ image client disposed before download handlers completed"); Disposed = true; }
            base.Dispose(disposing);
        }
    }
}
