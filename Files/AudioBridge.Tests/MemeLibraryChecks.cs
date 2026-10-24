using System.Text;
using System.Text.Json;
using System.Diagnostics;
using AudioBridge;

public static class MemeLibraryChecks
{
    public static byte[] Png => Convert.FromBase64String(Fixtures[0].Base64);
    private static readonly (string Extension, string MediaType, string Base64)[] Fixtures =
    [
        ("png", "image/png", "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGPkqbnEwMDAxMDAwMDAAAAPnAFeF4Fb7wAAAABJRU5ErkJggg=="),
        ("jpg", "image/jpeg", "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDDooor7c+HP//Z"),
        ("gif", "image/gif", "R0lGODdhAgACAIEAAAx80gAAAAAAAAAAACwAAAAAAgACAAAIBgABCAQQEAA7"),
        ("webp", "image/webp", "UklGRjwAAABXRUJQVlA4IDAAAAAQAgCdASoCAAIAAUAmJaACdLoB+AH4AAPIAP7uCmf+t/9QQH6dn/16IPEG/U0AAAA="),
        ("gif", "image/gif", "R0lGODlhAgACAIEAAAx80gAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAAAgACAAAIBgABCAQQEAAh+QQBCgABACwAAAAAAgACAIHSKB4AAAAAAAAAAAAIBgABCAQQEAA7"),
        ("webp", "image/webp", "UklGRsYAAABXRUJQVlA4WAoAAAACAAAAAQAAAQAAQU5JTQYAAAAAAAAAAABBTk1GSgAAAAAAAAAAAAEAAAEAAGQAAAJWUDggMgAAADABAJ0BKgIAAgABQCYloAADcAD+7gpn//rgP/+cB//zgP5lv//16P//PR//56P84AAAQU5NRkgAAAAAAAAAAAABAAABAABkAAAAVlA4IDAAAAA0AQCdASoCAAIAAAAmJaAAA3AA/vBTH//8gx/g1/mH0S///ewX/+7Bf/7sF/rYAAA=")
    ];

    public static Task<int> RunAsync()
    {
        var checks = new (string Name, Action Run)[]
        {
            ("missing library lists empty and unknown id cannot become a path", Empty),
            ("file and byte imports validate all real formats and GIF/WebP animations", Formats),
            ("import copies explicit local bytes and retains curated tags", CopyAndTags),
            ("curated native QQ faces persist, retain tags and resolve without image files", NativeFaces),
            ("native QQ faces reject malformed manifests, invalid bounds and ID collisions", NativeFaceSafety),
            ("native face and image writers retain every entry in one manifest", ConcurrentNativeFaces),
            ("byte upload owns its snapshot and never overwrites curated or unlisted assets", ByteImportSafety),
            ("tag updates persist without changing other entries or any asset bytes", TagUpdates),
            ("delete removes image and native entries while retaining source bytes and unrelated entries", Deletion),
            ("delete shares the writer lock with imports and edits without resurrecting deleted entries", ConcurrentDeletion),
            ("batch imports report independent commits and never overwrite duplicate IDs", BatchPartialResults),
            ("batch limits reject before writes and malformed items do not stop valid neighbors", BatchBoundaries),
            ("batch cancellation retains committed prefix and does not attempt later items", BatchCancellation),
            ("failed image import removes only its new copy and permits an explicit retry", ImportRollback),
            ("duplicate id and existing asset never overwrite", NoOverwrite),
            ("independent instances retain concurrent imports and original assets", ConcurrentImports),
            ("concurrent byte imports and tag updates preserve every manifest entry", ConcurrentTagUpdates),
            ("file import, byte upload and tag update timeouts leave the library untouched", WriterTimeout),
            ("manifest duplicate ids and invalid version are rejected", ManifestShape),
            ("manifest traversal, absolute and non-image filenames are rejected", Paths),
            ("library root cannot escape the project", RootScope),
            ("URL, UNC and relative import sources are rejected", ImportScope),
            ("corrupt, truncated and disguised formats are rejected", InvalidFormats),
            ("empty and oversize assets are rejected", Size),
            ("ids and tag count/content stay bounded", IdAndTags)
        };
        foreach (var check in checks) { check.Run(); Console.WriteLine("PASS meme library: " + check.Name); }
        var linked = Links();
        if (linked) Console.WriteLine("PASS meme library: reparse directory cannot escape the curated library");
        else Console.WriteLine("SKIP meme library link creation: Windows did not grant symbolic-link or junction creation; path-chain rejection remains implemented");
        return Task.FromResult(checks.Length + (linked ? 1 : 0));
    }

    private static void Empty()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root);
        Check(library.List().Count == 0, "missing library did not list empty");
        Reject(() => library.Get("unknown")); Reject(() => library.Get("../source.png"));
        Reject(() => library.ResolveSegment("qq_face_14"));
    }

    private static void Formats()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root);
        for (var index = 0; index < Fixtures.Length; index++)
        {
            var fixture = Fixtures[index]; var bytes = Convert.FromBase64String(fixture.Base64);
            var source = folder.Source(bytes, "source" + index + "." + fixture.Extension);
            var imported = library.Import("format_" + index, source, ["测试", "明确导入"]);
            var actual = library.Get(imported.Id);
            Check(actual.MediaType == fixture.MediaType && actual.Content.SequenceEqual(bytes), "real encoded fixture changed or rejected");
            _ = OneBotSegment.Image(actual.Content);
            var segment = library.ResolveSegment(imported.Id);
            Check(segment.Type == "image" && JsonSerializer.SerializeToElement(segment.Data).GetProperty("file").GetString() ==
                "base64://" + Convert.ToBase64String(bytes), "curated image resolution changed its actual bytes");
            var uploaded = library.ImportBytes("upload_" + index, bytes, ["上传", "真实格式"]);
            var storedUpload = new MemeLibrary(folder.Root).Get(uploaded.Id);
            Check(uploaded.MediaType == fixture.MediaType && uploaded.Content.SequenceEqual(bytes) &&
                  storedUpload.MediaType == fixture.MediaType && storedUpload.Content.SequenceEqual(bytes) &&
                  library.List().Single(x => x.Id == uploaded.Id).File == uploaded.Id + "." + fixture.Extension,
                  "byte import lost its detected format, extension or persisted bytes");
        }
        Check(library.List().Count == Fixtures.Length * 2, "import count wrong");
    }

    private static void CopyAndTags()
    {
        using var folder = new Folder(); var source = folder.Source(Png);
        var library = new MemeLibrary(folder.Root); library.Import("hello", source, [" 你好 ", "你好", "问候"]);
        File.WriteAllText(source, "source changed");
        var asset = library.Get("hello");
        Check(asset.Content.SequenceEqual(Png) && asset.Tags.SequenceEqual(new[] { "你好", "问候" }), "import did not copy or normalize tags");
        asset.Content[0] = 0;
        Check(library.Get("hello").Content.SequenceEqual(Png), "returned mutable bytes changed stored asset");
    }

    private static void NativeFaces()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root);
        library.ImportBytes("image", Png, ["本地图片"]);
        foreach (var faceId in new[] { 0, 14, 65535 })
        {
            var entry = library.AddFace(faceId, [" 微笑 ", "微笑", "QQ表情"]);
            Check(entry.Id == "qq_face_" + faceId && entry.FaceId == faceId && entry.File == "" &&
                entry.Tags.SequenceEqual(new[] { "微笑", "QQ表情" }), "native face registration or tag normalization wrong");
            var reopened = new MemeLibrary(folder.Root);
            var segment = reopened.ResolveSegment(entry.Id);
            Check(segment.Type == "face" && JsonSerializer.SerializeToElement(segment.Data).GetProperty("id").GetInt32() == faceId,
                "curated native face did not resolve to a face message segment");
            Reject(() => reopened.Get(entry.Id));
        }
        library.UpdateTags("qq_face_14", ["开心"]);
        Check(new MemeLibrary(folder.Root).List().Single(x => x.Id == "qq_face_14").Tags.SequenceEqual(new[] { "开心" }),
            "native face tags did not persist");
        Check(library.ResolveSegment("image").Type == "image" && library.Get("image").Content.SequenceEqual(Png),
            "native registration changed existing image resolution");
        Check(Directory.GetFiles(Path.GetDirectoryName(folder.Asset("manifest.json"))!).Length == 2,
            "native registration created image files");
        Reject(() => library.ResolveSegment("qq_face_15"));
    }

    private static void NativeFaceSafety()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root);
        foreach (var id in new[] { -1, 65536 }) Reject(() => library.AddFace(id, []));
        Reject(() => library.AddFace(14, ["bad\nlabel"]));
        library.ImportBytes("qq_face_14", Png, ["现有图片"]);
        var original = File.ReadAllBytes(folder.Asset("manifest.json"));
        Reject(() => library.AddFace(14, ["冲突"]));
        Check(File.ReadAllBytes(folder.Asset("manifest.json")).SequenceEqual(original) && library.Get("qq_face_14").Content.SequenceEqual(Png),
            "native registration overwrote an existing image ID");
        library.AddFace(15, []);
        original = File.ReadAllBytes(folder.Asset("manifest.json"));
        Reject(() => library.AddFace(15, []));
        Reject(() => library.ImportBytes("qq_face_15", Png, []));
        Check(File.ReadAllBytes(folder.Asset("manifest.json")).SequenceEqual(original) && !File.Exists(folder.Asset("qq_face_15.png")),
            "duplicate/native-image collision changed the manifest or copied an image");
        foreach (var entry in new MemeEntry[]
        {
            new("alias", "", [], 14), new("qq_face_014", "", [], 14), new("qq_face_14", "face.png", [], 14),
            new("qq_face_14", "", [], -1), new("qq_face_14", "", [], 65536), new("qq_face_14", null!, [], 14),
            new("qq_face_14", "", [], null)
        })
        { folder.Manifest([entry]); Reject(() => new MemeLibrary(folder.Root)); }
        folder.Manifest(Enumerable.Range(0, 500).Select(id => new MemeEntry("qq_face_" + id, "", [], id)).ToArray());
        original = File.ReadAllBytes(folder.Asset("manifest.json"));
        Reject(() => new MemeLibrary(folder.Root).AddFace(500, []));
        Check(File.ReadAllBytes(folder.Asset("manifest.json")).SequenceEqual(original) && new MemeLibrary(folder.Root).List().Count == 500,
            "full native library was overwritten with an unreadable manifest");
    }

    private static void ConcurrentNativeFaces()
    {
        using var folder = new Folder(); var first = new MemeLibrary(folder.Root); var second = new MemeLibrary(folder.Root);
        using var start = new ManualResetEventSlim();
        var native = Task.Run(() => { start.Wait(); for (var id = 0; id < 4; id++) first.AddFace(id, ["原生"]); });
        var images = Task.Run(() => { start.Wait(); for (var id = 0; id < 4; id++) second.ImportBytes("image_" + id, Png, ["图片"]); });
        start.Set(); Task.WhenAll(native, images).GetAwaiter().GetResult();
        var reopened = new MemeLibrary(folder.Root);
        Check(reopened.List().Count == 8 && reopened.List().Count(x => x.FaceId is not null) == 4,
            "concurrent native and image writes lost manifest entries");
        for (var id = 0; id < 4; id++)
            Check(reopened.ResolveSegment("qq_face_" + id).Type == "face" && reopened.Get("image_" + id).Content.SequenceEqual(Png),
                "concurrent native/image entry cannot resolve");
    }

    private static void ByteImportSafety()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root); var bytes = Png;
        var imported = library.ImportBytes("upload", bytes, [" 上传 ", "上传", "表情"]);
        var originalManifest = File.ReadAllBytes(folder.Asset("manifest.json"));
        bytes[0] = 0; imported.Content[1] = 0;
        var stored = library.Get("upload");
        Check(stored.Content.SequenceEqual(Png) && stored.Tags.SequenceEqual(new[] { "上传", "表情" }), "upload buffer or returned bytes changed stored image or tags");
        Reject(() => library.ImportBytes("upload", Convert.FromBase64String(Fixtures[2].Base64), ["覆盖"]));
        File.WriteAllText(folder.Asset("unlisted.png"), "existing user image");
        Reject(() => library.ImportBytes("unlisted", Png, []));
        Check(!File.Exists(folder.Asset("upload.gif")) &&
              File.ReadAllBytes(folder.Asset("upload.png")).SequenceEqual(Png) &&
              File.ReadAllText(folder.Asset("unlisted.png")) == "existing user image" &&
              File.ReadAllBytes(folder.Asset("manifest.json")).SequenceEqual(originalManifest), "duplicate byte upload changed manifest or existing assets");
        Reject(() => library.ImportBytes("null", null!, []));
    }

    private static void TagUpdates()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root);
        library.ImportBytes("hello", Png, ["旧标签"]);
        var otherBytes = Convert.FromBase64String(Fixtures[2].Base64);
        library.ImportBytes("other", otherBytes, ["保留"]);
        File.WriteAllText(folder.Asset("unlisted.png"), "unlisted original");
        var updated = library.UpdateTags("hello", [" 问候 ", "问候", "新标签"]);
        var reopened = new MemeLibrary(folder.Root);
        Check(updated.Id == "hello" && updated.File == "hello.png" && updated.Tags.SequenceEqual(new[] { "问候", "新标签" }) &&
              reopened.Get("hello").Tags.SequenceEqual(updated.Tags), "updated tags or original manifest filename did not persist");
        Check(reopened.List().Count == 2 && reopened.Get("other").Tags.SequenceEqual(new[] { "保留" }) &&
              File.ReadAllBytes(folder.Asset("hello.png")).SequenceEqual(Png) &&
              File.ReadAllBytes(folder.Asset("other.gif")).SequenceEqual(otherBytes) &&
              File.ReadAllText(folder.Asset("unlisted.png")) == "unlisted original", "tag update changed another entry or asset");
        var manifest = File.ReadAllBytes(folder.Asset("manifest.json"));
        Reject(() => library.UpdateTags("missing", ["未知"]));
        Reject(() => library.UpdateTags("hello", null!));
        Reject(() => library.UpdateTags("hello", Enumerable.Repeat("x", 9)));
        Reject(() => library.UpdateTags("hello", ["a\nb"]));
        Reject(() => library.UpdateTags("hello", [new string('a', 25)]));
        Reject(() => library.UpdateTags("hello", ["  "]));
        Check(File.ReadAllBytes(folder.Asset("manifest.json")).SequenceEqual(manifest), "rejected tag edit changed the manifest");
        // Deny manifest replacement while still allowing Load to read its current snapshot.
        using (var heldManifest = new FileStream(folder.Asset("manifest.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Reject(() => library.UpdateTags("hello", ["不能提交"]));
            Check(File.ReadAllBytes(folder.Asset("manifest.json")).SequenceEqual(manifest) &&
                  File.ReadAllBytes(folder.Asset("hello.png")).SequenceEqual(Png) &&
                  Directory.GetFiles(Path.GetDirectoryName(folder.Asset("manifest.json"))!, ".manifest-*.tmp").Length == 0,
                  "failed tag persistence changed original manifest or asset, or left a temporary manifest");
        }
        var maximumTags = Enumerable.Range(0, 8).Select(index => new string((char)('a' + index), 24)).ToArray();
        library.UpdateTags("hello", maximumTags);
        Check(reopened.Get("hello").Tags.SequenceEqual(maximumTags), "valid tag length or count boundary was rejected");
        library.UpdateTags("hello", []);
        Check(reopened.Get("hello").Tags.Count == 0 && reopened.Get("hello").Content.SequenceEqual(Png), "clearing tags changed original image or did not persist");
    }

    private static void Deletion()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root);
        library.ImportBytes("remove", Png, ["删除"]);
        library.ImportBytes("keep", Png, ["保留"]);
        library.AddFace(14, ["原生"]);
        File.WriteAllText(folder.Asset("unlisted.png"), "unlisted original");
        var manifest = File.ReadAllBytes(folder.Asset("manifest.json"));
        Check(!library.Delete("missing"), "unknown delete claimed an entry");
        foreach (var id in new[] { "../remove", "UPPER", "", "con", "remove.png" }) Reject(() => library.Delete(id));
        Check(File.ReadAllBytes(folder.Asset("manifest.json")).SequenceEqual(manifest), "unknown or invalid delete changed manifest");
        using (var heldManifest = new FileStream(folder.Asset("manifest.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Reject(() => library.Delete("remove"));
            Check(library.List().Any(x => x.Id == "remove") &&
                  File.ReadAllBytes(folder.Asset("manifest.json")).SequenceEqual(manifest) &&
                  Directory.GetFiles(Path.GetDirectoryName(folder.Asset("manifest.json"))!, ".manifest-*.tmp").Length == 0,
                "failed deletion changed the manifest or left a temporary file");
        }
        Check(library.Delete("remove") && library.Delete("qq_face_14") && !library.Delete("remove"),
            "image/native deletion or repeated delete result wrong");
        var reopened = new MemeLibrary(folder.Root);
        Check(reopened.List().Count == 1 && reopened.Get("keep").Tags.SequenceEqual(new[] { "保留" }) &&
              reopened.ResolveSegment("keep").Type == "image" &&
              File.ReadAllBytes(folder.Asset("remove.png")).SequenceEqual(Png) &&
              File.ReadAllBytes(folder.Asset("keep.png")).SequenceEqual(Png) &&
              File.ReadAllText(folder.Asset("unlisted.png")) == "unlisted original",
            "deletion changed retained image bytes, unrelated manifest entry or unlisted user file");
        Reject(() => library.Get("remove")); Reject(() => reopened.ResolveSegment("remove"));
        Reject(() => reopened.ResolveSegment("qq_face_14"));
        library.AddFace(14, ["重新收录"]);
        Check(reopened.ResolveSegment("qq_face_14").Type == "face", "deleted native entry cannot be explicitly added again");
    }

    private static void ConcurrentDeletion()
    {
        using var folder = new Folder(); var first = new MemeLibrary(folder.Root); var second = new MemeLibrary(folder.Root);
        for (var index = 0; index < 8; index++) first.ImportBytes("remove_" + index, Png, ["原标签"]);
        using var start = new ManualResetEventSlim();
        var deleting = Task.Run(() => { start.Wait(); for (var i = 0; i < 8; i++) Check(first.Delete("remove_" + i), "existing delete lost its entry"); });
        var importing = Task.Run(() => { start.Wait(); for (var i = 0; i < 8; i++) second.ImportBytes("keep_" + i, Png, ["保留"]); });
        var editing = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 8; i++)
                try { second.UpdateTags("remove_" + i, ["更新标签"]); }
                catch (ArgumentException) { } // A completed delete legitimately makes a later edit unknown.
        });
        start.Set(); Task.WhenAll(deleting, importing, editing).GetAwaiter().GetResult();
        var reopened = new MemeLibrary(folder.Root);
        Check(reopened.List().Count == 8 && reopened.List().All(x => x.Id.StartsWith("keep_", StringComparison.Ordinal)),
            "concurrent import/edit restored a deleted entry or lost a retained entry");
        for (var i = 0; i < 8; i++)
            Check(reopened.Get("keep_" + i).Content.SequenceEqual(Png) &&
                  File.ReadAllBytes(folder.Asset("remove_" + i + ".png")).SequenceEqual(Png),
                "concurrent delete altered source or newly imported bytes");
    }

    private static void BatchPartialResults()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root);
        library.ImportBytes("keep", Png, ["原标签"]);
        var encoded = Convert.ToBase64String(Png);
        var response = new MemeBatchImporter(library).Import(new([
            new("first", "first", ["手填"], encoded), new("duplicate", "keep", ["覆盖"], encoded),
            new("bad", "bad", [], Convert.ToBase64String("not an image"u8.ToArray())),
            new("last", "last", [], encoded), new("duplicate_id", "first", [], encoded)
        ]));
        Check(response.ImportedCount == 2 && response.FailedCount == 3 && response.NotAttemptedCount == 0 && !response.Cancelled &&
              response.Results.Select(x => x.ClientId).SequenceEqual(new[] { "first", "duplicate", "bad", "last", "duplicate_id" }) &&
              response.Results[0].Status == "imported" && response.Results[0].MediaType == "image/png" && response.Results[3].Status == "imported",
            "partial batch lost per-item order, success or independent errors");
        Check(library.List().Count == 3 && library.Get("keep").Tags.SequenceEqual(new[] { "原标签" }) &&
              library.Get("keep").Content.SequenceEqual(Png) && library.Get("first").Tags.SequenceEqual(new[] { "手填" }) &&
              library.Get("last").Content.SequenceEqual(Png), "batch changed existing asset or lost committed neighbors");
    }

    private static void BatchBoundaries()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root); var batch = new MemeBatchImporter(library);
        var encoded = Convert.ToBase64String(Png);
        Reject(() => batch.Import(new(null)));
        Reject(() => batch.Import(new([])));
        Reject(() => batch.Import(new(Enumerable.Range(0, 21).Select(i => new MemeBatchImportItem("row_" + i, "item_" + i, [], encoded)).ToArray())));
        var maximumEncoded = new string('A', ((OneBotMessageClient.MaximumImageBytes + 2) / 3) * 4 - 1) + "=";
        Reject(() => batch.Import(new([
            new("valid", "before_limit", [], encoded),
            new("large1", "large1", [], maximumEncoded), new("large2", "large2", [], maximumEncoded),
            new("large3", "large3", [], maximumEncoded), new("large4", "large4", [], maximumEncoded)
        ])));
        Check(library.List().Count == 0 && !File.Exists(folder.Asset("before_limit.png")), "oversized batch imported its valid prefix");
        var malformed = batch.Import(new([
            null, new(null, "missing_client", [], encoded), new("bad id", "bad_client", [], encoded),
            new("row", "invalid_tags", null, encoded), new("row", "invalid_base64", [], "%%%"),
            new("row", "duplicate_client", [], encoded), new("valid", "survivor", [], encoded)
        ]));
        Check(malformed.ImportedCount == 1 && malformed.FailedCount == 6 && library.List().Single().Id == "survivor",
            "malformed batch items interrupted valid neighbors or became imports");
        var oversized = batch.Import(new([
            new("too_large", "too_large", [], new string('A', 8 * 1024 * 1024)),
            new("valid", "after_oversized", [], encoded)
        ]));
        Check(oversized.ImportedCount == 1 && oversized.FailedCount == 1 && oversized.Results[0].ErrorCode == "image_too_large" &&
              !File.Exists(folder.Asset("too_large.png")) && library.Get("after_oversized").Content.SequenceEqual(Png),
            "oversized single item stopped its neighbor or copied bytes");
        var maximum = batch.Import(new(Enumerable.Range(0, 20).Select(i => new MemeBatchImportItem("max_" + i, "max_" + i, [], encoded)).ToArray()));
        Check(maximum.ImportedCount == 20 && maximum.FailedCount == 0, "valid twenty-item batch was rejected");
    }

    private static void BatchCancellation()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root);
        var encoded = Convert.ToBase64String(Png);
        var request = new MemeBatchImportRequest([new("first", "committed", [], encoded), new("second", "unattempted", [], encoded)]);
        using var stopping = new CancellationTokenSource();
        using var held = new Mutex(false, library.ImportMutexName);
        held.WaitOne();
        using var started = new ManualResetEventSlim();
        MemeBatchImportResponse? response = null; Exception? failure = null;
        var worker = new Thread(() => { started.Set(); try { response = new MemeBatchImporter(library).Import(request, stopping.Token); } catch (Exception error) { failure = error; } });
        worker.Start();
        try
        {
            Check(started.Wait(TimeSpan.FromSeconds(2)) && SpinWait.SpinUntil(() => (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(2)),
                "batch worker did not reach the held writer mutex");
            stopping.Cancel();
        }
        finally { held.ReleaseMutex(); worker.Join(); }
        Check(failure is null && response is { ImportedCount: 1, NotAttemptedCount: 1, FailedCount: 0, Cancelled: true } &&
              response.Results[0].Status == "imported" && response.Results[1].Status == "not_attempted" &&
              library.List().Single().Id == "committed" && !File.Exists(folder.Asset("unattempted.png")),
            "cancellation hid the committed first item or attempted a later item");
        var cancelled = new MemeBatchImporter(library).Import(new([new("pre", "pre_cancelled", [], encoded)]), stopping.Token);
        Check(cancelled.NotAttemptedCount == 1 && cancelled.ImportedCount == 0 && !File.Exists(folder.Asset("pre_cancelled.png")),
            "pre-cancelled batch wrote bytes");
    }

    private static void ImportRollback()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root);
        library.ImportBytes("original", Png, ["保留"]);
        var manifest = File.ReadAllBytes(folder.Asset("manifest.json"));
        using (var heldManifest = new FileStream(folder.Asset("manifest.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Reject(() => library.ImportBytes("retry", Png, []));
            Check(!File.Exists(folder.Asset("retry.png")) && File.ReadAllBytes(folder.Asset("manifest.json")).SequenceEqual(manifest) &&
                  library.Get("original").Content.SequenceEqual(Png), "failed import retained an orphan copy or changed original assets");
        }
        library.ImportBytes("retry", Png, []);
        Check(library.Get("retry").Content.SequenceEqual(Png), "failed import could not explicitly retry the same ID");
        folder.Manifest(Enumerable.Range(0, 500).Select(i => new MemeEntry("qq_face_" + i, "", [], i)).ToArray());
        manifest = File.ReadAllBytes(folder.Asset("manifest.json"));
        Reject(() => library.ImportBytes("over_capacity", Png, []));
        Check(!File.Exists(folder.Asset("over_capacity.png")) && File.ReadAllBytes(folder.Asset("manifest.json")).SequenceEqual(manifest),
            "capacity failure copied an orphan asset or changed manifest");
    }

    private static void NoOverwrite()
    {
        using var folder = new Folder(); var source = folder.Source(Png); var library = new MemeLibrary(folder.Root);
        library.Import("hello", source, []); var original = File.ReadAllBytes(folder.Asset("hello.png"));
        Reject(() => library.Import("hello", source, []));
        File.WriteAllText(folder.Asset("other.png"), "user asset");
        Reject(() => library.Import("other", source, []));
        Check(File.ReadAllBytes(folder.Asset("hello.png")).SequenceEqual(original) && File.ReadAllText(folder.Asset("other.png")) == "user asset", "asset overwritten");
    }

    private static void ConcurrentImports()
    {
        using var folder = new Folder(); var source = folder.Source(Png);
        var first = new MemeLibrary(folder.Root);
        // Spelling variations of the same Windows directory must use one process-wide name.
        var second = new MemeLibrary(folder.Root.ToUpperInvariant(), "Files/./Memes/");
        Check(first.ImportMutexName == second.ImportMutexName, "equivalent library directories use different writer mutexes");
        first.Import("original", source, ["保留"]);
        var originalBytes = File.ReadAllBytes(folder.Asset("original.png"));
        File.WriteAllText(folder.Asset("unlisted.png"), "original unlisted user file");
        using var start = new ManualResetEventSlim();
        var writers = new[] { (Library: first, Prefix: "first_"), (Library: second, Prefix: "second_") }
            .Select(writer => Task.Run(() =>
            {
                start.Wait();
                for (var index = 0; index < 8; index++) writer.Library.Import(writer.Prefix + index, source, [writer.Prefix]);
            })).ToArray();
        start.Set();
        Task.WhenAll(writers).GetAwaiter().GetResult();
        var entries = new MemeLibrary(folder.Root).List();
        Check(entries.Count == 17 && entries.Select(x => x.Id).Distinct().Count() == 17, "concurrent manifest replacement lost an entry");
        foreach (var prefix in new[] { "first_", "second_" })
            for (var index = 0; index < 8; index++)
                Check(first.Get(prefix + index).Content.SequenceEqual(Png), "concurrent import id cannot resolve its bytes");
        Check(first.Get("original").Tags.SequenceEqual(new[] { "保留" }) &&
              File.ReadAllBytes(folder.Asset("original.png")).SequenceEqual(originalBytes) &&
              File.ReadAllText(folder.Asset("unlisted.png")) == "original unlisted user file", "existing entry or asset changed");
    }

    private static void WriterTimeout()
    {
        using var folder = new Folder(); var source = folder.Source(Png); var library = new MemeLibrary(folder.Root);
        library.Import("original", source, []);
        var originalManifest = File.ReadAllBytes(folder.Asset("manifest.json"));
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Exception? ownerFailure = null;
        var owner = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(false, library.ImportMutexName);
                mutex.WaitOne();
                try { held.Set(); release.Wait(); }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception error) { ownerFailure = error; held.Set(); }
        }) { IsBackground = true };
        owner.Start();
        try
        {
            Check(held.Wait(TimeSpan.FromSeconds(2)) && ownerFailure is null, "test owner could not take writer mutex");
            var blockedWrites = new Action[]
            {
                () => library.ImportBytes("blocked_bytes", Png, []),
                () => library.UpdateTags("original", ["blocked tag"]),
                () => library.Delete("original"),
                () => library.AddFace(14, ["blocked face"])
            }.Select(write => Task.Factory.StartNew(() =>
            {
                var elapsed = Stopwatch.StartNew();
                try { write(); throw new Exception("blocked byte upload or tag update did not time out"); }
                catch (TimeoutException) { Check(elapsed.Elapsed < TimeSpan.FromSeconds(6), "byte upload or tag update exceeded its lock deadline"); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            var timer = Stopwatch.StartNew();
            try { library.Import("blocked", source, []); throw new Exception("blocked import did not time out"); }
            catch (TimeoutException error) { Check(error.Message.Contains("未复制新素材", StringComparison.Ordinal), "timeout did not report no new asset"); }
            Check(timer.Elapsed < TimeSpan.FromSeconds(6), "writer lock wait exceeded its bounded deadline");
            Task.WhenAll(blockedWrites).GetAwaiter().GetResult();
            Check(!File.Exists(folder.Asset("blocked.png")) && !File.Exists(folder.Asset("blocked_bytes.png")) &&
                  File.ReadAllBytes(folder.Asset("manifest.json")).SequenceEqual(originalManifest), "timed-out writer changed asset or manifest");
        }
        finally { release.Set(); owner.Join(); }
        library.Import("recovered", source, []);
        Check(library.List().Count == 2 && library.Get("original").Content.SequenceEqual(Png), "released writer lock did not recover without losing original");
    }

    private static void ConcurrentTagUpdates()
    {
        using var folder = new Folder(); var source = folder.Source(Png);
        var first = new MemeLibrary(folder.Root); var second = new MemeLibrary(folder.Root, "Files/./Memes/");
        first.Import("original", source, ["旧标签"]); first.ImportBytes("kept", Png, ["保留"]);
        var originalBytes = File.ReadAllBytes(folder.Asset("original.png"));
        using var start = new ManualResetEventSlim();
        var writers = new[] { (Library: first, Prefix: "first_"), (Library: second, Prefix: "second_") }
            .Select(writer => Task.Run(() =>
            {
                start.Wait();
                for (var index = 0; index < 8; index++) writer.Library.ImportBytes(writer.Prefix + index, Png, [writer.Prefix]);
            })).ToList();
        writers.Add(Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < 8; index++) second.UpdateTags("original", ["更新" + index]);
        }));
        start.Set(); Task.WhenAll(writers).GetAwaiter().GetResult();
        var reopened = new MemeLibrary(folder.Root);
        Check(reopened.List().Count == 18 && reopened.List().Select(x => x.Id).Distinct().Count() == 18 &&
              reopened.Get("original").Tags.SequenceEqual(new[] { "更新7" }) &&
              reopened.Get("kept").Tags.SequenceEqual(new[] { "保留" }) &&
              File.ReadAllBytes(folder.Asset("original.png")).SequenceEqual(originalBytes), "concurrent tag update lost an import, another entry or original bytes");
        foreach (var prefix in new[] { "first_", "second_" })
            for (var index = 0; index < 8; index++)
            {
                var asset = reopened.Get(prefix + index);
                Check(asset.Content.SequenceEqual(Png) && asset.Tags.SequenceEqual(new[] { prefix }), "concurrent byte import lost its content or tags");
            }
    }

    private static void ManifestShape()
    {
        using var folder = new Folder();
        folder.Manifest(new[] { new MemeEntry("same", "a.png", []), new MemeEntry("same", "b.png", []) });
        Reject(() => new MemeLibrary(folder.Root));
        folder.Manifest([], 2); Reject(() => new MemeLibrary(folder.Root));
        File.WriteAllText(folder.Asset("manifest.json"), "not JSON"); Reject(() => new MemeLibrary(folder.Root));
    }

    private static void Paths()
    {
        using var folder = new Folder();
        foreach (var file in new[] { "../source.png", "sub/source.png", "sub\\source.png", "C:\\source.png", "/source.png", "x.txt", "a..png", "x.png:payload" })
        { folder.Manifest([new("hello", file, [])]); Reject(() => new MemeLibrary(folder.Root)); }
    }

    private static void RootScope()
    {
        using var folder = new Folder();
        Reject(() => new MemeLibrary(folder.Root, "../outside"));
        Reject(() => new MemeLibrary(folder.Root, folder.Root));
        Reject(() => new MemeLibrary(folder.Root, "."));
    }

    private static void ImportScope()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root);
        foreach (var source in new[] { "https://example.com/test.png", "source.png", "\\\\server\\share\\x.png" })
            Reject(() => library.Import("hello", source, []));
    }

    private static void InvalidFormats()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root);
        var corrupt = Png; corrupt[corrupt.Length - 1] ^= 1;
        var variants = new List<byte[]> { Encoding.UTF8.GetBytes("<script>not an image</script>"), corrupt, Png[..24] };
        variants.AddRange(Fixtures.Select(x => Convert.FromBase64String(x.Base64)[..^2]));
        foreach (var bytes in variants)
        {
            Reject(() => library.Import("bad", folder.Source(bytes), []));
            Reject(() => library.ImportBytes("bad_bytes", bytes, []));
        }
        Check(library.List().Count == 0 && !File.Exists(folder.Asset("bad_bytes.png")), "invalid upload created a manifest entry or asset");
        Directory.CreateDirectory(Path.GetDirectoryName(folder.Asset("wrong.gif"))!);
        File.WriteAllBytes(folder.Asset("wrong.gif"), Png);
        folder.Manifest([new("wrong", "wrong.gif", [])]); Reject(() => library.Get("wrong")); Reject(() => library.ResolveSegment("wrong"));
    }

    private static void Size()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root);
        Reject(() => library.Import("empty", folder.Source([]), []));
        Reject(() => library.Import("large", folder.Source(new byte[OneBotMessageClient.MaximumImageBytes + 1]), []));
        Reject(() => library.ImportBytes("empty", [], []));
        Reject(() => library.ImportBytes("large", new byte[OneBotMessageClient.MaximumImageBytes + 1], []));
    }

    private static void IdAndTags()
    {
        using var folder = new Folder(); var library = new MemeLibrary(folder.Root); var source = folder.Source(Png);
        foreach (var id in new[] { "../x", "Hello", "", "con", "nul", "com1", new string('a', 49) })
        {
            Reject(() => library.Import(id, source, []));
            Reject(() => library.ImportBytes(id, Png, []));
            Reject(() => library.UpdateTags(id, []));
            Reject(() => library.Delete(id));
        }
        Reject(() => library.Import("a", source, Enumerable.Repeat("x", 9)));
        Reject(() => library.Import("a", source, ["a\nb"]));
        Reject(() => library.Import("a", source, [new string('a', 25)]));
        Reject(() => library.ImportBytes("a", Png, Enumerable.Repeat("x", 9)));
        Reject(() => library.ImportBytes("a", Png, ["a\nb"]));
        Reject(() => library.ImportBytes("a", Png, [new string('a', 25)]));
        Reject(() => library.ImportBytes("a", Png, [" "]));
    }

    private static bool Links()
    {
        using var folder = new Folder(); var source = folder.Source(Png);
        Directory.CreateDirectory(folder.Asset("."));
        var link = folder.Asset("linked.png");
        var fileLinkCreated = false;
        try { File.CreateSymbolicLink(link, source); fileLinkCreated = true; }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        if (fileLinkCreated)
        {
            var library = new MemeLibrary(folder.Root);
            Reject(() => library.ImportBytes("linked", Png, []));
            folder.Manifest([new("linked", "linked.png", [])]);
            Reject(() => library.UpdateTags("linked", ["链接"]));
            Reject(() => library.Delete("linked"));
            Reject(() => new MemeLibrary(folder.Root));
            File.Delete(link);
        }
        var outside = Path.Combine(folder.Root, "outside"); Directory.CreateDirectory(outside);
        var linkedDirectory = Path.Combine(folder.Root, "linked-library");
        var linkedLibrary = new MemeLibrary(folder.Root, "linked-library");
        try { Directory.CreateSymbolicLink(linkedDirectory, outside); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            // Junction creation does not require Windows' symbolic-link privilege.
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", linkedDirectory, outside }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            _ = process.StandardOutput.ReadToEnd(); _ = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) return fileLinkCreated;
        }
        Reject(() => new MemeLibrary(folder.Root, "linked-library"));
        Reject(() => linkedLibrary.ImportBytes("escape", Png, []));
        Reject(() => linkedLibrary.AddFace(14, []));
        Reject(() => linkedLibrary.UpdateTags("escape", []));
        Reject(() => linkedLibrary.Delete("escape"));
        Directory.Delete(linkedDirectory);
        return true;
    }

    private static void Reject(Action operation)
    { try { operation(); } catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException) { return; } throw new Exception("unsafe library operation accepted"); }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }

    private sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine("Cache", "meme-library-checks", Guid.NewGuid().ToString("N")));
        public Folder() => Directory.CreateDirectory(Root);
        public string Asset(string name) => Path.Combine(Root, "Files", "Memes", name);
        public string Source(byte[] content, string name = "source.png") { var path = Path.Combine(Root, name); File.WriteAllBytes(path, content); return path; }
        public void Manifest(IReadOnlyList<MemeEntry> entries, int version = 1)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Asset("manifest.json"))!);
            File.WriteAllText(Asset("manifest.json"), JsonSerializer.Serialize(new { version, items = entries }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
        public void Dispose()
        {
            var expected = Path.GetFullPath(Path.Combine("Cache", "meme-library-checks")) + Path.DirectorySeparatorChar;
            if (!Root.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("test cleanup outside cache");
            Directory.Delete(Root, recursive: true);
        }
    }
}
