using System.Text.Json.Nodes;
using AudioBridge;

public static class ScheduledMessageChecks
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 0, 59, 0, TimeSpan.Zero); // Shanghai 08:59.
    private static readonly long[] Groups = [10];
    private static readonly long[] Users = [20];

    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("empty reads do not create schedules or background work", Defaults),
            ("once claims are durable and never retried after reopen", OnceClaims),
            ("daily occurrences use Shanghai date and resist clock rollback", DailyAndRollback),
            ("edits and deletion invalidate outstanding revisions", Revisions),
            ("pause consumes due work and invalidates outstanding claims", Paused),
            ("offline and expired occurrences do not accumulate catch-up sends", OfflineAndExpired),
            ("group and private authorization is rechecked before sending", Authorization),
            ("late transport evidence stays accurate without retrying", CompletionEvidence),
            ("concurrent instances atomically reserve each occurrence once", ConcurrentClaims),
            ("concurrent edits reject stale revisions without overwriting", ConcurrentEdits),
            ("invalid schedules and payloads leave existing data untouched", Validation),
            ("task count and serialized bytes stay bounded", Capacity),
            ("corrupt, duplicate and incompatible documents are never reset", Corruption),
            ("result metadata does not copy message content or private context", Metadata),
            ("linked storage paths cannot read or overwrite external files", LinkedPaths)
        };
        foreach (var check in checks)
        {
            await check.Run().WaitAsync(TimeSpan.FromSeconds(30));
            Console.WriteLine("PASS scheduled messages: " + check.Name);
        }
        return checks.Length;
    }

    private static Task Defaults()
    {
        using var fixture = new Fixture();
        Check(fixture.Store.List().Count == 0, "empty store fabricated tasks");
        Check(fixture.Store.ClaimDue(Groups, Users, false, Start).Count == 0, "empty store fabricated claims");
        Check(!Directory.Exists(Path.Combine(fixture.Root, "Results")), "read-only operations created data or background files");
        return Task.CompletedTask;
    }

    private static Task OnceClaims()
    {
        using var fixture = new Fixture();
        var task = fixture.Save(Once());
        Check(task.Revision == 1 && task.LastRun is null, "new task has prior results");
        fixture.Clock.Now = Start.AddMinutes(1);
        var claim = fixture.Claim().Single();
        var disk = new ScheduledMessageStore(fixture.Root, fixture.Clock);
        Check(disk.List().Single().LastRun?.Status == "claimed", "returned claim was not durable");
        Check(disk.ClaimDue(Groups, Users, false, Start).Count == 0, "reopen retried a pending claim");
        Check(disk.IsCurrentAndAuthorized(claim, Groups, Users, false), "fresh durable claim is not current");
        Check(disk.Complete(claim, "api_accepted", "12345"), "success receipt was not recorded");
        Check(!disk.Complete(claim, "api_accepted", "12345"), "same completion mutated completed work");
        Check(!disk.IsCurrentAndAuthorized(claim, Groups, Users, false) && disk.ClaimDue(Groups, Users, false, Start).Count == 0,
            "completed occurrence could be sent again");
        Check(Directory.GetFiles(Path.Combine(fixture.Root, "Results", "local")).Length == 1, "atomic write left backup or temporary copies");
        return Task.CompletedTask;
    }

    private static Task DailyAndRollback()
    {
        using var fixture = new Fixture();
        fixture.Save(new() { Enabled = true, TargetId = 10, ScheduleType = "daily", DailyTime = "09:00", Text = "早上好" });
        Check(fixture.Claim().Count == 0, "daily occurrence fired early");
        fixture.Clock.Now = Start.AddMinutes(1);
        var first = fixture.Claim().Single();
        Check(first.DueAt == new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.FromHours(8)), "daily occurrence used machine timezone");
        fixture.Store.Complete(first, "preview");
        fixture.Clock.Now = Start.AddMinutes(-1);
        Check(fixture.Claim().Count == 0, "clock rollback rearmed daily occurrence");
        fixture.Clock.Now = Start.AddMinutes(1).AddDays(1);
        var second = fixture.Claim().Single();
        Check(second.OccurrenceId != first.OccurrenceId && second.DueAt == first.DueAt.AddDays(1), "next Shanghai day did not get an independent occurrence");
        fixture.Clock.Now = Start.AddMinutes(1);
        Check(fixture.Claim().Count == 0, "rollback replayed a consumed day");
        return Task.CompletedTask;
    }

    private static Task Revisions()
    {
        using var fixture = new Fixture();
        var task = fixture.Save(Once());
        fixture.Clock.Now = Start.AddMinutes(1);
        var old = fixture.Claim().Single();
        var updated = fixture.Store.Save(task.Definition with { Text = "第二版", OnceAt = Start.AddMinutes(2) }, Groups, Users, task.Id, task.Revision);
        Check(updated.Id == task.Id && updated.Revision == 2 && updated.LastRun is null, "edit replaced identity or retained old work");
        Check(!fixture.Store.IsCurrentAndAuthorized(old, Groups, Users, false) && !fixture.Store.Complete(old, "api_accepted", "123"), "old revision mutated new task");
        Reject(() => fixture.Store.Save(updated.Definition, Groups, Users, task.Id, task.Revision));
        Reject(() => fixture.Store.Delete(task.Id, task.Revision));
        fixture.Clock.Now = Start.AddMinutes(2);
        var current = fixture.Claim().Single();
        Check(fixture.Store.Delete(task.Id, updated.Revision), "current task was not deleted");
        Check(!fixture.Store.IsCurrentAndAuthorized(current, Groups, Users, false) && !fixture.Store.Complete(current, "cancelled"), "deleted task accepted late work");
        return Task.CompletedTask;
    }

    private static Task Paused()
    {
        using var fixture = new Fixture();
        fixture.Save(Once());
        fixture.Clock.Now = Start.AddMinutes(1);
        Check(fixture.Store.ClaimDue(Groups, Users, true, Start).Count == 0 && fixture.Store.List().Single().LastRun?.Status == "skipped_paused", "pause retained due work");
        Check(fixture.Claim().Count == 0, "resume replayed paused occurrence");
        var second = fixture.Save(Once() with { OnceAt = Start.AddMinutes(2) });
        fixture.Clock.Now = Start.AddMinutes(2);
        var pending = fixture.Claim().Single();
        Check(!fixture.Store.IsCurrentAndAuthorized(pending, Groups, Users, true), "paused send recheck accepted work");
        fixture.Store.ClaimDue(Groups, Users, true, Start);
        Check(!fixture.Store.IsCurrentAndAuthorized(pending, Groups, Users, false) && fixture.Store.List().Single(t => t.Id == second.Id).LastRun?.Status == "skipped_paused",
            "resume revived a pending claim invalidated by pause");
        return Task.CompletedTask;
    }

    private static Task OfflineAndExpired()
    {
        using var fixture = new Fixture();
        fixture.Save(Once());
        fixture.Clock.Now = Start.AddMinutes(1).AddSeconds(2);
        Check(fixture.Store.ClaimDue(Groups, Users, false, fixture.Clock.Now).Count == 0 && fixture.Store.List().Single().LastRun?.Status == "skipped_offline", "startup sent an earlier occurrence");
        var expiry = fixture.Save(Once() with { OnceAt = Start.AddMinutes(2) });
        fixture.Clock.Now = Start.AddMinutes(3).AddTicks(1);
        Check(fixture.Claim().Count == 0 && fixture.Store.List().Single(t => t.Id == expiry.Id).LastRun?.Status == "skipped_expired", "late polling created a backlog");
        var orphan = fixture.Save(Once() with { OnceAt = Start.AddMinutes(4) });
        fixture.Clock.Now = Start.AddMinutes(4);
        var claim = fixture.Claim().Single();
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        var reopened = new ScheduledMessageStore(fixture.Root, fixture.Clock);
        Check(reopened.ClaimDue(Groups, Users, false, fixture.Clock.Now).Count == 0 &&
            reopened.List().Single(t => t.Id == orphan.Id).LastRun?.Status == "delivery_unknown" && !reopened.IsCurrentAndAuthorized(claim, Groups, Users, false),
            "new owner retried or revived an interrupted delivery");
        fixture.Save(new() { Enabled = true, TargetId = 10, ScheduleType = "daily", DailyTime = "09:10", Text = "不补发" });
        fixture.Clock.Now = Start.AddDays(10).AddHours(1);
        Check(fixture.Claim().Count == 0 && fixture.Store.List().Last().LastRun?.Status == "skipped_expired", "many missed days created multiple attempts");
        return Task.CompletedTask;
    }

    private static Task Authorization()
    {
        using var fixture = new Fixture();
        Reject(() => fixture.Save(Once() with { TargetId = 99 }));
        Reject(() => fixture.Save(Once() with { TargetType = "private", TargetId = 10 }));
        fixture.Save(Once() with { TargetType = "private", TargetId = 20 });
        fixture.Clock.Now = Start.AddMinutes(1);
        var claim = fixture.Claim().Single();
        Check(!fixture.Store.IsCurrentAndAuthorized(claim, Groups, [], false), "removed private target remained authorized");
        fixture.Store.ClaimDue(Groups, [], false, Start);
        Check(!fixture.Store.IsCurrentAndAuthorized(claim, Groups, Users, false) && fixture.Store.List().Single().LastRun?.Status == "skipped_not_authorized", "restoring whitelist revived withdrawn work");
        fixture.Save(Once() with { OnceAt = Start.AddMinutes(2) });
        fixture.Clock.Now = Start.AddMinutes(2);
        Check(fixture.Store.ClaimDue([], Users, false, Start).Count == 0, "due task escaped current group whitelist");
        return Task.CompletedTask;
    }

    private static Task CompletionEvidence()
    {
        using var fixture = new Fixture();
        fixture.Save(Once());
        fixture.Clock.Now = Start.AddMinutes(1);
        var claim = fixture.Claim().Single();
        Reject(() => fixture.Store.Complete(claim, "claimed"));
        Reject(() => fixture.Store.Complete(claim, "api_accepted"));
        Reject(() => fixture.Store.Complete(claim, "api_accepted", "0"));
        Reject(() => fixture.Store.Complete(claim, "preview", "123"));
        fixture.Clock.Now = claim.DueAt.AddSeconds(61);
        fixture.Claim();
        Check(!fixture.Store.IsCurrentAndAuthorized(claim, Groups, Users, false), "expired work remained sendable");
        Check(fixture.Store.Complete(claim, "delivery_unknown"), "uncertain submitted HTTP was mislabeled never sent");
        Check(fixture.Store.Complete(claim, "api_accepted", "-123"), "late real receipt could not correct cancellation race");
        Check(!fixture.Store.Complete(claim, "delivery_unknown") && fixture.Store.List().Single().LastRun?.MessageId == "-123", "weaker evidence overwrote a real receipt");
        Check(fixture.Claim().Count == 0, "unknown or successful delivery was retried");
        return Task.CompletedTask;
    }

    private static async Task ConcurrentClaims()
    {
        using var fixture = new Fixture();
        fixture.Save(Once());
        fixture.Clock.Now = Start.AddMinutes(1);
        var stores = Enumerable.Range(0, 8).Select(_ => new ScheduledMessageStore(fixture.Root, fixture.Clock)).ToArray();
        var results = await Task.WhenAll(stores.Select(store => Task.Run(() => store.ClaimDue(Groups, Users, false, Start))));
        Check(results.Sum(result => result.Count) == 1 && fixture.Store.List().Count == 1, "concurrent owners double claimed or overwrote tasks");
    }

    private static async Task ConcurrentEdits()
    {
        using var fixture = new Fixture();
        var task = fixture.Save(Once());
        var stores = Enumerable.Range(0, 6).Select(_ => new ScheduledMessageStore(fixture.Root, fixture.Clock)).ToArray();
        var results = await Task.WhenAll(stores.Select((store, i) => Task.Run(() =>
        {
            try { store.Save(task.Definition with { Text = "版本" + i }, Groups, Users, task.Id, task.Revision); return true; }
            catch (InvalidOperationException) { return false; }
        })));
        Check(results.Count(result => result) == 1 && fixture.Store.List().Single().Revision == 2, "stale concurrent editors overwrote each other");
    }

    private static Task Validation()
    {
        using var fixture = new Fixture();
        fixture.Save(Once());
        var bytes = File.ReadAllBytes(fixture.Path);
        foreach (var invalid in new[]
        {
            Once() with { Name = "" }, Once() with { TargetType = "other" }, Once() with { TargetId = 0 },
            Once() with { TimeZone = "UTC" }, Once() with { ScheduleType = "weekly" }, Once() with { OnceAt = null },
            Once() with { DailyTime = "09:00" }, Once() with { OnceAt = Start },
            Once() with { ScheduleType = "daily", OnceAt = null, DailyTime = "9:00" },
            Once() with { ScheduleType = "daily", OnceAt = null, DailyTime = "24:00" },
            Once() with { Text = new string('中', 3001) }, Once() with { Text = "\0" }, Once() with { Text = "  " },
            Once() with { MemeId = "../outside" }, Once() with { MemeId = "https://example.test/image" }, Once() with { MemeId = "con" }
        }) Reject(() => fixture.Save(invalid));
        Reject(() => fixture.Store.Save(Once(), Groups, Users, expectedRevision: 1));
        Reject(() => fixture.Store.Save(Once(), Groups, Users, Guid.NewGuid().ToString("D")));
        Reject(() => fixture.Store.Delete("../outside", 1));
        Check(File.ReadAllBytes(fixture.Path).SequenceEqual(bytes), "invalid update rewrote existing data");
        fixture.Save(Once() with { Text = "", MemeId = "qq_face_14" });
        fixture.Save(Once() with { Text = "第一行\n第二行", MemeId = "roger" });
        Check(fixture.Store.List().Count == 3, "valid meme-only or multiline content rejected");
        return Task.CompletedTask;
    }

    private static Task Capacity()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < ScheduledMessageStore.MaximumTasks; index++) fixture.Save(Once() with { Text = new string('中', 3000) });
        var bytes = File.ReadAllBytes(fixture.Path);
        Reject(() => fixture.Save(Once()));
        Check(fixture.Store.List().Count == 64 && bytes.Length <= ScheduledMessageStore.MaximumBytes && File.ReadAllBytes(fixture.Path).SequenceEqual(bytes), "task limit changed or invalid insertion wrote data");
        File.WriteAllBytes(fixture.Path, new byte[ScheduledMessageStore.MaximumBytes + 1]);
        RejectData(() => new ScheduledMessageStore(fixture.Root, fixture.Clock));
        Check(new FileInfo(fixture.Path).Length == ScheduledMessageStore.MaximumBytes + 1, "oversized store was silently reset");
        return Task.CompletedTask;
    }

    private static Task Corruption()
    {
        using var fixture = new Fixture();
        fixture.Save(Once());
        var original = File.ReadAllText(fixture.Path);
        var variants = new List<string> { "", "{", "{\"version\":1,\"version\":1,\"tasks\":[]}" };
        var version = JsonNode.Parse(original)!; version["version"] = 2; variants.Add(version.ToJsonString());
        var duplicate = JsonNode.Parse(original)!; ((JsonArray)duplicate["tasks"]!).Add(duplicate["tasks"]![0]!.DeepClone()); variants.Add(duplicate.ToJsonString());
        var missing = JsonNode.Parse(original)!; ((JsonObject)missing["tasks"]![0]!["definition"]!).Remove("enabled"); variants.Add(missing.ToJsonString());
        var invalid = JsonNode.Parse(original)!; invalid["tasks"]![0]!["revision"] = 0; variants.Add(invalid.ToJsonString());
        var unknown = JsonNode.Parse(original)!; unknown["tasks"]![0]!["definition"]!["secretContext"] = "不得接收的额外字段"; variants.Add(unknown.ToJsonString());
        foreach (var bad in variants)
        {
            File.WriteAllText(fixture.Path, bad);
            var bytes = File.ReadAllBytes(fixture.Path);
            RejectData(() => new ScheduledMessageStore(fixture.Root, fixture.Clock));
            RejectData(() => fixture.Store.ClaimDue(Groups, Users, false, Start));
            Check(File.ReadAllBytes(fixture.Path).SequenceEqual(bytes), "corrupt data was overwritten with defaults");
        }
        return Task.CompletedTask;
    }

    private static Task Metadata()
    {
        using var fixture = new Fixture();
        fixture.Save(Once() with { Text = "固定的发送内容", MemeId = "roger" });
        fixture.Clock.Now = Start.AddMinutes(1);
        var claim = fixture.Claim().Single();
        fixture.Store.Complete(claim, "preview");
        var document = JsonNode.Parse(File.ReadAllText(fixture.Path))!;
        var metadata = document["tasks"]![0]!["lastRun"]!.ToJsonString();
        Check(!metadata.Contains("固定的发送内容", StringComparison.Ordinal) && !metadata.Contains("roger", StringComparison.Ordinal) &&
            ((JsonObject)document["tasks"]![0]!["lastRun"]!).Count == 6, "result metadata copied payload or private context");
        Check(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).SequenceEqual(new[] { fixture.Path }), "store created conversation history or extra payload copies");
        return Task.CompletedTask;
    }

    private static Task LinkedPaths()
    {
        using var fixture = new Fixture();
        using var external = new Fixture();
        var externalFile = Path.Combine(external.Root, "original.json");
        File.WriteAllText(externalFile, "外部原始数据");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Path)!);
        var fileLinked = false;
        try { File.CreateSymbolicLink(fixture.Path, externalFile); fileLinked = true; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            Console.WriteLine("SKIP scheduled file link scenario: OS did not grant symbolic-link creation.");
        }
        if (fileLinked)
        {
            try
            {
                RejectData(() => new ScheduledMessageStore(fixture.Root, fixture.Clock));
                RejectData(() => fixture.Store.Save(Once(), Groups, Users));
                Check(File.ReadAllText(externalFile) == "外部原始数据", "linked file was overwritten");
            }
            finally { File.Delete(fixture.Path); }
        }
        var local = Path.GetDirectoryName(fixture.Path)!;
        Directory.Delete(local);
        try { Directory.CreateSymbolicLink(local, external.Root); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            Console.WriteLine("SKIP scheduled directory link scenario: OS did not grant symbolic-link creation.");
            return Task.CompletedTask;
        }
        try
        {
            RejectData(() => new ScheduledMessageStore(fixture.Root, fixture.Clock));
            RejectData(() => fixture.Store.Save(Once(), Groups, Users));
            Check(Directory.GetFiles(external.Root).SequenceEqual(new[] { externalFile }), "linked directory received files");
        }
        finally { Directory.Delete(local); }
        return Task.CompletedTask;
    }

    private static ScheduledMessageDraft Once() => new() { Enabled = true, TargetId = 10, OnceAt = Start.AddMinutes(1), Text = "合成定时内容" };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return; }
        throw new Exception("invalid scheduled operation was accepted");
    }
    private static void RejectData(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("unsafe or corrupt schedule data was accepted");
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string parent = System.IO.Path.GetFullPath(System.IO.Path.Combine("Cache", "scheduled-message-checks"));
        public string Root { get; }
        public string Path => System.IO.Path.Combine(Root, "Results", "local", "scheduled-messages.json");
        public Clock Clock { get; } = new();
        public ScheduledMessageStore Store { get; }
        public Fixture()
        {
            Root = System.IO.Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root); Store = new(Root, Clock);
        }
        public ScheduledMessageTask Save(ScheduledMessageDraft draft) => Store.Save(draft, Groups, Users);
        public IReadOnlyList<ScheduledMessageClaim> Claim() => Store.ClaimDue(Groups, Users, false, Start);
        public void Dispose()
        {
            if (!System.IO.Path.GetFullPath(Root).StartsWith(parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试目录不在预定范围内。");
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
