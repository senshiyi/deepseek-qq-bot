using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AudioBridge;

public static class AdminProfileChecks
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("absent store returns defaults without creating a file", Defaults),
            ("persona and explicit notes survive a new store instance", Persistence),
            ("per-contact personas override only the fields they set", ContactPersonas),
            ("legacy persona files keep group sharing disabled and new switches persist", LegacyGroupSettings),
            ("persona confirmation compares and saves under one cross-instance lock", ConcurrentPersonaConfirmation),
            ("updates keep identity and deletion leaves no private backup", UpdateAndDelete),
            ("contact operations and private prompt contexts stay isolated", ContactIsolation),
            ("concurrent instances preserve every note and the persona", ConcurrentInstances),
            ("invalid persona values leave the original store unchanged", InvalidPersona),
            ("invalid identifiers, notes and contact limits reject without writes", InvalidMemories),
            ("corrupt or incompatible documents are refused without reset", Corruption),
            ("storage size and model context remain bounded", Bounds),
            ("linked store paths cannot read or overwrite another location", LinkedPaths)
        };
        foreach (var check in checks)
        {
            await check.Run().WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine("PASS admin profile: " + check.Name);
        }
        return checks.Length;
    }

    private static Task ContactPersonas()
    {
        using var folder = new Folder();
        var store = new BotProfileStore(folder.Root);
        var global = store.GetPersona();
        Check(store.GetContactPersona(20) is null && store.EffectivePersona(20) == global, "a contact without a persona did not fall back to the global one");
        store.SaveContactPersona(new(20, "小柚", null, "哥哥", null));
        var saved = store.GetContactPersona(20)!;
        Check(saved is { UserId: 20, Name: "小柚", UserAddress: "哥哥" } && saved.Personality is null, "saved persona kept the wrong fields");
        var effective = new BotProfileStore(folder.Root).EffectivePersona(20);
        Check(effective.Name == "小柚" && effective.UserAddress == "哥哥" &&
              effective.Personality == global.Personality && effective.SpeakingStyle == global.SpeakingStyle,
            "per-contact fields did not override only what they set");
        Check(store.EffectivePersona(21) == global, "another contact inherited someone else's persona");
        store.SaveContactPersona(new(20, "小柚2", "更黏人", "哥哥", null));
        Check(store.EffectivePersona(20) is { Name: "小柚2", Personality: "更黏人" }, "saving again did not replace the same contact's persona");
        Check(store.DeleteContactPersona(20) && !store.DeleteContactPersona(20), "delete did not remove exactly one persona");
        Check(store.EffectivePersona(20) == global, "deleting the persona did not restore the global one");
        foreach (var invalid in new[]
        {
            new ContactPersona(0, "x", null, null, null), new ContactPersona(20, null, null, null, null),
            new ContactPersona(20, new string('x', 17), null, null, null), new ContactPersona(20, null, null, new string('x', 33), null)
        })
        {
            try { store.SaveContactPersona(invalid); throw new Exception("invalid contact persona was accepted"); }
            catch (ArgumentException) { }
        }
        return Task.CompletedTask;
    }

    private static Task Defaults()
    {
        using var folder = new Folder();
        var store = new BotProfileStore(folder.Root);
        Check(store.GetPersona() == new PersonaSettings(), "missing store did not use exact defaults");
        Check(store.ListMemories(123).Count == 0 && store.BuildPrivateMemoryContext(123) == "", "missing store fabricated memories");
        Check(!File.Exists(folder.Path) && Directory.GetFiles(folder.Root).Length == 0, "read-only defaults created a store or history file");
        return Task.CompletedTask;
    }

    private static Task Persistence()
    {
        using var folder = new Folder();
        var first = new BotProfileStore(folder.Root);
        var persona = new PersonaSettings { Name = "合成角色", Personality = "合成的性格资料", UserAddress = "同学",
            SpeakingStyle = "先回答问题", ReplyMaxCharacters = 120, MemeProbabilityPercent = 25 };
        first.SavePersona(persona);
        var note = first.AddMemory(123, "  合成联系人喜欢短回答  ");
        var second = new BotProfileStore(folder.Root);
        Check(second.GetPersona() == persona && second.ListMemories(123).Single() == note, "restart did not restore persona and notes");
        Check(note.Text == "合成联系人喜欢短回答" && Guid.TryParseExact(note.Id, "D", out _), "explicit note was not normalized or assigned a UUID");
        Check(note.CreatedAt != default && note.CreatedAt == note.UpdatedAt, "new note timestamps invalid");
        Check(Directory.GetFiles(folder.Root).SequenceEqual(new[] { folder.Path }), "write left a temporary or backup file");
        return Task.CompletedTask;
    }

    private static Task UpdateAndDelete()
    {
        using var folder = new Folder();
        var store = new BotProfileStore(folder.Root);
        var a = store.AddMemory(123, "需要删除的合成私密条目");
        var b = store.AddMemory(456, "另一个联系人的条目");
        var updated = new BotProfileStore(folder.Root).UpdateMemory(123, a.Id.ToUpperInvariant(), "修改后的合成条目");
        Check(updated.Id == a.Id && updated.UserId == 123 && updated.CreatedAt == a.CreatedAt && updated.UpdatedAt >= a.UpdatedAt,
            "update changed identity or creation time");
        Check(store.ListMemories(123).Single() == updated, "existing instance did not reread another instance's edit");
        Check(store.DeleteMemory(123, a.Id) && !store.DeleteMemory(123, a.Id), "deletion did not report existing/missing item");
        Check(new BotProfileStore(folder.Root).ListMemories(123).Count == 0 && store.ListMemories(456).Single() == b,
            "deletion did not persist or damaged another contact");
        using var document = JsonDocument.Parse(File.ReadAllBytes(folder.Path));
        Check(document.RootElement.GetProperty("memories").GetArrayLength() == 1, "deleted item remained in active JSON");
        Check(Directory.GetFiles(folder.Root).SequenceEqual(new[] { folder.Path }), "deletion retained a private history/backup file");
        return Task.CompletedTask;
    }

    private static Task LegacyGroupSettings()
    {
        using var folder = new Folder();
        var store = new BotProfileStore(folder.Root);
        var note = store.AddMemory(123, "合成联系人笔记保留");
        var legacy = JsonNode.Parse(File.ReadAllText(folder.Path))!;
        legacy["persona"]!.AsObject().Remove("usePersonaInGroups");
        legacy["persona"]!.AsObject().Remove("shareContactMemoriesInGroups");
        File.WriteAllText(folder.Path, legacy.ToJsonString());
        var before = File.ReadAllBytes(folder.Path);
        var original = store.GetPersona();
        Check(!original.UsePersonaInGroups && !original.ShareContactMemoriesInGroups &&
              File.ReadAllBytes(folder.Path).SequenceEqual(before), "legacy persona enabled sharing or was rewritten during read");
        var updated = original with { UsePersonaInGroups = true, ShareContactMemoriesInGroups = true };
        store.SavePersona(updated);
        var reopened = new BotProfileStore(folder.Root);
        Check(reopened.GetPersona() == updated && reopened.ListMemories(123).Single() == note,
            "group switches did not persist or saving them changed existing notes");
        return Task.CompletedTask;
    }

    private static async Task ConcurrentPersonaConfirmation()
    {
        using var folder = new Folder();
        var first = new BotProfileStore(folder.Root);
        var note = first.AddMemory(123, "并发人格确认保留的合成笔记");
        var original = first.GetPersona();
        var expected = PersonaLearning.HashPersona(original);
        var proposals = new[] { original with { Personality = "合成候选甲" }, original with { Personality = "合成候选乙" } };
        using var start = new ManualResetEventSlim();
        var pending = proposals.Select(proposal => Task.Run(() =>
        { start.Wait(); return new BotProfileStore(folder.Root).TrySavePersona(proposal, expected); })).ToArray();
        start.Set();
        var results = await Task.WhenAll(pending);
        var winner = Array.IndexOf(results, true);
        Check(results.Count(value => value) == 1 && first.GetPersona() == proposals[winner] && first.ListMemories(123).Single() == note,
            "concurrent confirmations both succeeded, overwrote their winner, or changed notes");
        var before = File.ReadAllBytes(folder.Path);
        Check(!first.TrySavePersona(original, expected) && File.ReadAllBytes(folder.Path).SequenceEqual(before),
            "stale confirmation changed the current persona");
    }

    private static Task ContactIsolation()
    {
        using var folder = new Folder();
        var store = new BotProfileStore(folder.Root);
        var a = store.AddMemory(123, "甲的私密偏好标记");
        var b = store.AddMemory(456, "乙的私密偏好标记");
        var before = File.ReadAllBytes(folder.Path);
        Reject(() => store.UpdateMemory(456, a.Id, "企图跨联系人修改"));
        Check(!store.DeleteMemory(456, a.Id) && !store.DeleteMemory(123, b.Id), "cross-contact deletion succeeded");
        Check(File.ReadAllBytes(folder.Path).SequenceEqual(before), "cross-contact operation rewrote the store");
        var contextA = store.BuildPrivateMemoryContext(123);
        var contextB = store.BuildPrivateMemoryContext(456);
        Check(contextA.Contains(a.Text, StringComparison.Ordinal) && !contextA.Contains(b.Text, StringComparison.Ordinal) &&
              contextB.Contains(b.Text, StringComparison.Ordinal) && !contextB.Contains(a.Text, StringComparison.Ordinal) &&
              store.BuildPrivateMemoryContext(789) == "", "private prompt leaked another contact's notes");
        store.SavePersona(new PersonaSettings { Personality = "角色的独立设定" });
        Check(store.ListMemories(123).Single() == a && store.ListMemories(456).Single() == b, "persona save modified contact notes");
        Check(!store.GetPersona().Personality.Contains(a.Text, StringComparison.Ordinal), "private notes polluted the shared persona");
        return Task.CompletedTask;
    }

    private static async Task ConcurrentInstances()
    {
        using var folder = new Folder();
        var stores = Enumerable.Range(0, 16).Select(_ => new BotProfileStore(folder.Root)).ToArray();
        var persona = new PersonaSettings { Name = "并发角色" };
        var writers = stores.Select((store, i) => Task.Run(() => store.AddMemory(123, "合成并发条目" + i))).ToArray();
        var personaWriter = Task.Run(() => new BotProfileStore(folder.Root).SavePersona(persona));
        var results = await Task.WhenAll(writers);
        await personaWriter;
        var final = new BotProfileStore(folder.Root);
        Check(final.ListMemories(123).Count == 16 && results.All(x => final.ListMemories(123).Contains(x)), "concurrent add lost an update");
        Check(final.GetPersona() == persona, "concurrent note writer overwrote persona");
        var updates = results.Select((note, i) => Task.Run(() => stores[i].UpdateMemory(123, note.Id, "并发修改" + i)));
        await Task.WhenAll(updates);
        Check(final.ListMemories(123).All(x => x.Text.StartsWith("并发修改", StringComparison.Ordinal)), "concurrent updates were lost");
        await Task.WhenAll(results.Take(8).Select((note, i) => Task.Run(() => stores[i].DeleteMemory(123, note.Id))));
        Check(final.ListMemories(123).Count == 8 && final.GetPersona() == persona, "concurrent deletion resurrected notes or changed persona");
    }

    private static Task InvalidPersona()
    {
        using var folder = new Folder();
        var store = new BotProfileStore(folder.Root);
        store.AddMemory(123, "保持不变的合成条目");
        var before = File.ReadAllBytes(folder.Path);
        var valid = new PersonaSettings();
        foreach (var invalid in new[]
        {
            valid with { Name = "" }, valid with { Name = new string('x', 17) }, valid with { Name = "a\nb" },
            valid with { Personality = " " }, valid with { Personality = new string('x', 2001) },
            valid with { UserAddress = "" }, valid with { UserAddress = new string('x', 33) },
            valid with { SpeakingStyle = "" }, valid with { SpeakingStyle = new string('x', 1001) },
            valid with { Name = null! }, valid with { Personality = null! }, valid with { UserAddress = null! }, valid with { SpeakingStyle = null! },
            valid with { ReplyMaxCharacters = 39 }, valid with { ReplyMaxCharacters = 3001 },
            valid with { MemeProbabilityPercent = -1 }, valid with { MemeProbabilityPercent = 101 }
        }) Reject(() => store.SavePersona(invalid));
        Reject(() => store.SavePersona(null!));
        Check(File.ReadAllBytes(folder.Path).SequenceEqual(before), "invalid persona changed persisted data");
        (valid with { Name = new string('x', 16), UserAddress = new string('x', 32), Personality = new string('x', 2000),
            SpeakingStyle = new string('x', 1000), ReplyMaxCharacters = 40, MemeProbabilityPercent = 0 }).Validate();
        (valid with { ReplyMaxCharacters = 3000, MemeProbabilityPercent = 100 }).Validate();
        return Task.CompletedTask;
    }

    private static Task InvalidMemories()
    {
        using var folder = new Folder();
        var store = new BotProfileStore(folder.Root);
        var original = store.AddMemory(123, "保留的合成条目");
        var before = File.ReadAllBytes(folder.Path);
        foreach (var userId in new long[] { 0, -1, long.MinValue })
        {
            Reject(() => store.ListMemories(userId)); Reject(() => store.BuildPrivateMemoryContext(userId));
            Reject(() => store.AddMemory(userId, "x")); Reject(() => store.UpdateMemory(userId, original.Id, "x"));
            Reject(() => store.DeleteMemory(userId, original.Id));
        }
        foreach (var id in new[] { "", "../x", "not-a-uuid", Guid.Empty.ToString("D"), Guid.NewGuid().ToString("N"), null! })
        { Reject(() => store.UpdateMemory(123, id, "x")); Reject(() => store.DeleteMemory(123, id)); }
        foreach (var text in new[] { "", " \r\n ", new string('x', 1001), "a\0b", null! })
        { Reject(() => store.AddMemory(123, text)); Reject(() => store.UpdateMemory(123, original.Id, text)); }
        Reject(() => store.UpdateMemory(123, Guid.NewGuid().ToString("D"), "未知条目"));
        Check(File.ReadAllBytes(folder.Path).SequenceEqual(before), "invalid memory operation rewrote the store");
        for (var i = 1; i < 32; i++) store.AddMemory(123, i == 1 ? new string('x', 1000) : "合成条目" + i);
        before = File.ReadAllBytes(folder.Path);
        Reject(() => store.AddMemory(123, "第33条"));
        Check(File.ReadAllBytes(folder.Path).SequenceEqual(before), "33rd note changed the store");
        store.AddMemory(456, "另一联系人的额度独立");
        Check(store.ListMemories(123).Count == 32 && store.ListMemories(456).Count == 1, "note cap applied globally instead of per contact");
        return Task.CompletedTask;
    }

    private static Task Corruption()
    {
        using var folder = new Folder();
        var store = new BotProfileStore(folder.Root);
        store.AddMemory(123, "原始合成条目");
        var original = File.ReadAllText(folder.Path);
        var variants = new List<string> { "", "not JSON", "null", "{}", "[]", "{\"version\":1,\"version\":1}",
            original.Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal) };
        foreach (var edit in new Action<JsonObject>[]
        {
            x => x.Remove("persona"), x => x["persona"] = null, x => x["memories"] = null,
            x => x["persona"]!.AsObject().Remove("name"), x => x["persona"]!["extra"] = "unexpected",
            x => x["persona"]!["replyMaxCharacters"] = 2,
            x => x["persona"]!["usePersonaInGroups"] = "true", x => x["persona"]!["shareContactMemoriesInGroups"] = null,
            x => x["memories"]![0]!["id"] = "bad-id", x => x["memories"]![0]!["userId"] = 0,
            x => x["memories"]![0]!["text"] = null, x => x["memories"]![0]!["updatedAt"] = "2000-01-01T00:00:00Z",
            x => x["memories"]!.AsArray().Add(x["memories"]![0]!.DeepClone()),
            x => x["memories"]!.AsArray().Add((JsonNode?)null)
        })
        {
            var node = JsonNode.Parse(original)!.AsObject(); edit(node); variants.Add(node.ToJsonString());
        }
        foreach (var invalid in variants)
        {
            File.WriteAllText(folder.Path, invalid);
            var before = File.ReadAllBytes(folder.Path);
            RejectCorrupt(() => new BotProfileStore(folder.Root));
            RejectCorrupt(() => store.GetPersona()); RejectCorrupt(() => store.ListMemories(123));
            RejectCorrupt(() => store.SavePersona(new PersonaSettings())); RejectCorrupt(() => store.AddMemory(123, "不得覆盖"));
            Check(File.ReadAllBytes(folder.Path).SequenceEqual(before), "corrupt file was silently reset or changed");
        }
        File.WriteAllText(folder.Path, original);
        Check(store.ListMemories(123).Single().Text == "原始合成条目", "explicitly restored valid data could not be reread");
        return Task.CompletedTask;
    }

    private static Task Bounds()
    {
        using var folder = new Folder();
        var store = new BotProfileStore(folder.Root);
        for (var i = 0; i < 8; i++) store.AddMemory(123, new string('中', 999) + i);
        store.AddMemory(456, "不应出现在其他联系人上下文的标记");
        var context = store.BuildPrivateMemoryContext(123);
        Check(context.Length <= 4000 && context.Length > 3000 && !context.Contains("不应", StringComparison.Ordinal), "model context exceeded bound or leaked another contact");
        var now = DateTimeOffset.UtcNow;
        var memories = new List<ContactMemory>();
        byte[] accepted = [];
        while (true)
        {
            memories.Add(new(Guid.NewGuid().ToString("D"), 1000 + memories.Count, new string('中', 1000), now, now));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, persona = new PersonaSettings(), memories }, Json);
            if (bytes.Length > 512 * 1024) break;
            accepted = bytes;
        }
        File.WriteAllBytes(folder.Path, accepted);
        Reject(() => store.AddMemory(999999, new string('中', 1000)));
        Check(File.ReadAllBytes(folder.Path).SequenceEqual(accepted), "oversized write replaced the valid store");
        File.WriteAllBytes(folder.Path, new byte[512 * 1024 + 1]);
        RejectCorrupt(() => store.GetPersona());
        Check(new FileInfo(folder.Path).Length == 512 * 1024 + 1, "oversized read reset the source");
        return Task.CompletedTask;
    }

    private static Task LinkedPaths()
    {
        using var folder = new Folder();
        using var outside = new Folder();
        var outsideStore = new BotProfileStore(outside.Root);
        outsideStore.AddMemory(123, "外部合成条目");
        var before = File.ReadAllBytes(outside.Path);
        var tested = false;
        try { File.CreateSymbolicLink(folder.Path, outside.Path); tested = true; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
        if (tested)
        {
            try { RejectCorrupt(() => new BotProfileStore(folder.Root)); }
            finally { File.Delete(folder.Path); }
            Check(File.ReadAllBytes(outside.Path).SequenceEqual(before), "linked file changed outside data");
        }
        var link = System.IO.Path.Combine(folder.Root, "linked-project");
        var directoryLinked = false;
        try { Directory.CreateSymbolicLink(link, outside.Root); directoryLinked = true; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
        if (directoryLinked)
        {
            try { RejectCorrupt(() => new BotProfileStore(link)); }
            finally { Directory.Delete(link); }
            Check(File.ReadAllBytes(outside.Path).SequenceEqual(before), "linked project changed outside data");
        }
        if (!tested || !directoryLinked) Console.WriteLine("SKIP admin profile: unavailable OS symbolic-link privilege limits path checks");
        return Task.CompletedTask;
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException) { return; }
        throw new Exception("invalid profile operation was accepted");
    }

    private static void RejectCorrupt(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("corrupt or linked profile was accepted");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Folder : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "audio-bridge-profile-check-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "bot-profile.local.json");
        public Folder() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
