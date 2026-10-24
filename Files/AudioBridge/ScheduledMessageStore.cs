using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AudioBridge;

/// <summary>
/// Local fixed-content schedule storage only: never calls a model, sends a message, or starts a worker.
/// Claims are persisted before return. An interrupted or uncertain delivery is never retried automatically.
/// </summary>
public sealed class ScheduledMessageStore
{
    public const int MaximumTasks = 64;
    public const int MaximumBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan DeliveryWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShanghaiOffset = TimeSpan.FromHours(8);
    private static readonly HashSet<string> CompletionStatuses = new(StringComparer.Ordinal)
    {
        "api_accepted", "preview", "delivery_unknown", "failed", "cancelled", "skipped_paused",
        "skipped_expired", "skipped_offline", "skipped_busy", "skipped_not_authorized", "account_changed_paused"
    };
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true
    };
    private readonly string directory;
    private readonly string path;
    private readonly string mutexName;
    private readonly TimeProvider clock;

    public ScheduledMessageStore(string projectDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        EnsureNoLinks(project);
        if (!Directory.Exists(project)) throw new DirectoryNotFoundException("定时消息的项目目录不存在。");
        directory = Path.Combine(project, "Results", "local");
        path = Path.Combine(directory, "scheduled-messages.json");
        mutexName = "Global\\DeepseekQqBot.Schedules." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        clock = timeProvider ?? TimeProvider.System;
        WithLock(() => Load());
    }

    public IReadOnlyList<ScheduledMessageTask> List() => WithLock(() => (IReadOnlyList<ScheduledMessageTask>)Load().Tasks
        .OrderBy(task => task.CreatedAt).ThenBy(task => task.Id, StringComparer.Ordinal).ToArray());

    /// <summary>Creates when id is null; updates require the current revision. Caller validates MemeId against its active library.</summary>
    public ScheduledMessageTask Save(ScheduledMessageDraft draft, IEnumerable<long> allowedGroups,
        IEnumerable<long> allowedPrivateUsers, string? id = null, long? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);
        if (!Authorized(draft, allowedGroups, allowedPrivateUsers)) throw new ArgumentException("定时消息的目标不在当前授权白名单中。");
        if (id is not null) ValidateId(id);
        if (id is null && expectedRevision is not null || id is not null && expectedRevision is null)
            throw new ArgumentException("修改定时消息须提供当前版本。");
        return WithLock(() =>
        {
            var document = Load();
            var now = clock.GetUtcNow();
            if (draft.Enabled && draft.ScheduleType == "once" && draft.OnceAt <= now)
                throw new ArgumentException("启用的一次性任务须指定未来时间；过去的任务不会补发。");
            ScheduledMessageTask task;
            if (id is null)
            {
                if (document.Tasks.Count >= MaximumTasks) throw new ArgumentException("最多保存64条定时消息。");
                task = new(Guid.NewGuid().ToString("D"), 1, draft, now, now, now, null);
                document.Tasks.Add(task);
            }
            else
            {
                var index = document.Tasks.FindIndex(value => value.Id == id);
                if (index < 0) throw new ArgumentException("定时消息不存在。");
                var previous = document.Tasks[index];
                if (previous.Revision != expectedRevision) throw new InvalidOperationException("定时消息已被修改，请刷新后重试。");
                // Edits start a new revision at the monotonic watermark, invalidating all old work.
                var watermark = now > previous.EvaluatedThrough ? now : previous.EvaluatedThrough;
                task = previous with { Revision = checked(previous.Revision + 1), Definition = draft,
                    UpdatedAt = now > previous.UpdatedAt ? now : previous.UpdatedAt, EvaluatedThrough = watermark, LastRun = null };
                document.Tasks[index] = task;
            }
            Write(document);
            return task;
        });
    }

    public bool Delete(string id, long expectedRevision)
    {
        ValidateId(id);
        return WithLock(() =>
        {
            var document = Load();
            var index = document.Tasks.FindIndex(task => task.Id == id);
            if (index < 0) return false;
            if (document.Tasks[index].Revision != expectedRevision) throw new InvalidOperationException("定时消息已被修改，请刷新后重试。");
            document.Tasks.RemoveAt(index); Write(document); return true;
        });
    }

    /// <summary>
    /// Poll while the host is alive, including while paused. Missed occurrences are consumed without catch-up.
    /// sessionStartedAt is the actual start of this owner, not a newly supplied time on each poll.
    /// </summary>
    public IReadOnlyList<ScheduledMessageClaim> ClaimDue(IEnumerable<long> allowedGroups,
        IEnumerable<long> allowedPrivateUsers, bool paused, DateTimeOffset sessionStartedAt)
    {
        var groups = AllowedIds(allowedGroups);
        var users = AllowedIds(allowedPrivateUsers);
        if (sessionStartedAt == default) throw new ArgumentException("须提供当前宿主的启动时间。");
        return WithLock(() =>
        {
            var document = Load();
            var now = clock.GetUtcNow();
            var claims = new List<ScheduledMessageClaim>();
            var changed = false;
            for (var index = 0; index < document.Tasks.Count; index++)
            {
                var task = document.Tasks[index];
                var allowed = Authorized(task.Definition, groups, users);
                if (task.LastRun is { Status: "claimed" } pending)
                {
                    var invalid = paused ? "skipped_paused" : !allowed ? "skipped_not_authorized" :
                        pending.ClaimedAt < sessionStartedAt ? "delivery_unknown" : now > pending.DueAt + DeliveryWindow ? "skipped_expired" : null;
                    if (invalid is not null)
                    {
                        task = task with { LastRun = pending with { Status = invalid, CompletedAt = Later(now, pending.ClaimedAt) } };
                        changed = true;
                    }
                }
                var due = LatestDue(task.Definition, now);
                if (task.Definition.Enabled && due is { } at && at > task.EvaluatedThrough)
                {
                    var status = paused ? "skipped_paused" : !allowed ? "skipped_not_authorized" :
                        at < sessionStartedAt ? "skipped_offline" : now > at + DeliveryWindow ? "skipped_expired" : "claimed";
                    var occurrence = OccurrenceId(task.Id, task.Revision, at);
                    task = task with { EvaluatedThrough = at,
                        LastRun = new(occurrence, at, now, status == "claimed" ? null : now, status, null) };
                    if (status == "claimed") claims.Add(new(task.Id, task.Revision, occurrence, at, task.Definition));
                    changed = true;
                }
                document.Tasks[index] = task;
            }
            if (changed) Write(document); // Every returned claim is durable before a caller can send it.
            return (IReadOnlyList<ScheduledMessageClaim>)claims.ToArray();
        });
    }

    /// <summary>Recheck pause, time window, current revision and fresh target authorization just before the send.</summary>
    public bool IsCurrentAndAuthorized(ScheduledMessageClaim claim, IEnumerable<long> allowedGroups,
        IEnumerable<long> allowedPrivateUsers, bool paused)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var groups = AllowedIds(allowedGroups);
        var users = AllowedIds(allowedPrivateUsers);
        if (paused) return false;
        return WithLock(() =>
        {
            var task = Load().Tasks.SingleOrDefault(value => value.Id == claim.TaskId);
            var now = clock.GetUtcNow();
            return Matches(task, claim) && task!.Definition.Enabled && task.Definition == claim.Definition &&
                task.LastRun!.Status == "claimed" && now >= claim.DueAt && now <= claim.DueAt + DeliveryWindow &&
                Authorized(task.Definition, groups, users);
        });
    }

    public bool Complete(ScheduledMessageClaim claim, string status, string? messageId = null)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!CompletionStatuses.Contains(status)) throw new ArgumentException("定时消息执行状态无效。");
        if (status == "api_accepted" ? !long.TryParse(messageId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) || id == 0 : messageId is not null)
            throw new ArgumentException("仅有效的QQ成功回执可记录消息编号。");
        return WithLock(() =>
        {
            var document = Load();
            var index = document.Tasks.FindIndex(task => task.Id == claim.TaskId);
            if (index < 0 || !Matches(document.Tasks[index], claim)) return false;
            var task = document.Tasks[index];
            var run = task.LastRun!;
            // A real late receipt is stronger evidence than a cancellation/expiry racing with submitted HTTP.
            if (run.Status != "claimed" && status != "api_accepted" && status != "delivery_unknown" || run.Status == "api_accepted") return false;
            task = task with { LastRun = run with { Status = status, MessageId = messageId,
                CompletedAt = Later(clock.GetUtcNow(), run.ClaimedAt) } };
            document.Tasks[index] = task; Write(document); return true;
        });
    }

    private static bool Matches(ScheduledMessageTask? task, ScheduledMessageClaim claim) =>
        task is not null && task.Revision == claim.Revision && task.LastRun is { } run &&
        run.OccurrenceId == claim.OccurrenceId && run.DueAt == claim.DueAt && task.Definition == claim.Definition;

    private static DateTimeOffset? LatestDue(ScheduledMessageDraft draft, DateTimeOffset now)
    {
        if (draft.ScheduleType == "once") return draft.OnceAt <= now ? draft.OnceAt : null;
        var local = now.ToOffset(ShanghaiOffset);
        var time = TimeOnly.ParseExact(draft.DailyTime!, "HH:mm", CultureInfo.InvariantCulture);
        var candidate = new DateTimeOffset(local.Year, local.Month, local.Day, time.Hour, time.Minute, 0, ShanghaiOffset);
        return candidate <= now ? candidate : candidate.AddDays(-1);
    }

    private static string OccurrenceId(string taskId, long revision, DateTimeOffset dueAt) =>
        taskId + ":" + revision.ToString(CultureInfo.InvariantCulture) + ":" + dueAt.UtcTicks.ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

    private static HashSet<long> AllowedIds(IEnumerable<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.ToHashSet();
        if (result.Any(id => id <= 0)) throw new ArgumentException("授权目标QQ号或群号须为正整数。");
        return result;
    }

    private static bool Authorized(ScheduledMessageDraft draft, IEnumerable<long> groups, IEnumerable<long> users) =>
        (draft.TargetType == "group" ? AllowedIds(groups) : AllowedIds(users)).Contains(draft.TargetId);

    private static void ValidateDraft(ScheduledMessageDraft draft)
    {
        PersonaSettings.ValidateText(draft.Name, 80, "任务名称", false);
        if (draft.TargetType is not ("group" or "private") || draft.TargetId <= 0 || draft.TimeZone != "Asia/Shanghai")
            throw new ArgumentException("须选择授权群或私聊及Asia/Shanghai时区。");
        if (draft.ScheduleType == "once")
        {
            if (draft.OnceAt is null || draft.OnceAt == default(DateTimeOffset) || draft.DailyTime is not null)
                throw new ArgumentException("一次性任务须只提供有效的onceAt。");
        }
        else if (draft.ScheduleType == "daily")
        {
            if (draft.OnceAt is not null || draft.DailyTime is null || draft.DailyTime.Length != 5 ||
                !TimeOnly.TryParseExact(draft.DailyTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ArgumentException("每日任务须只提供HH:mm格式的dailyTime。");
        }
        else throw new ArgumentException("只支持once或daily任务。");
        if (draft.Text is null || draft.Text.Length > OneBotMessageClient.MaximumTextCharacters ||
            draft.Text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) || string.IsNullOrWhiteSpace(draft.Text) && draft.MemeId is null)
            throw new ArgumentException("定时消息须有正文或表情，正文最多3000字符。");
        if (draft.MemeId is { } meme && (!Regex.IsMatch(meme, "^[a-z0-9][a-z0-9_-]{0,47}$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(meme, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new ArgumentException("须提供已入库的表情ID，不接受路径或URL。");
    }

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("定时消息库正被占用，请稍后重试。");
            return action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private ScheduleDocument Load()
    {
        EnsureNoLinks(path);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new InvalidDataException("定时消息路径不是普通文件。");
            return new(1, []);
        }
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("定时消息库为空或超过2MiB，未重置文件。");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("定时消息库在读取期间改变，未重置文件。");
        EnsureNoLinks(path);
        try
        {
            using var parsed = JsonDocument.Parse(bytes);
            CheckProperties(parsed.RootElement, ["version", "tasks"]);
            foreach (var task in parsed.RootElement.GetProperty("tasks").EnumerateArray())
            {
                CheckProperties(task, ["id", "revision", "definition", "createdAt", "updatedAt", "evaluatedThrough", "lastRun"]);
                CheckProperties(task.GetProperty("definition"), ["name", "enabled", "targetType", "targetId", "scheduleType", "onceAt", "dailyTime", "timeZone", "text", "memeId"]);
                if (task.GetProperty("lastRun") is { ValueKind: not JsonValueKind.Null } run)
                    CheckProperties(run, ["occurrenceId", "dueAt", "claimedAt", "completedAt", "status", "messageId"]);
            }
            var document = JsonSerializer.Deserialize<ScheduleDocument>(bytes, Json) ?? throw new JsonException();
            ValidateDocument(document); return document;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { throw new InvalidDataException("定时消息库格式或内容无效，已拒绝读取且未重置文件。", error); }
    }

    private void Write(ScheduleDocument document)
    {
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaximumBytes) throw new ArgumentException("定时消息库超过2MiB，本次修改未保存。");
        EnsureNoLinks(directory); Directory.CreateDirectory(directory); EnsureNoLinks(directory); EnsureNoLinks(path);
        var temporary = Path.Combine(directory, "scheduled-messages." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            EnsureNoLinks(temporary); EnsureNoLinks(path); File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateDocument(ScheduleDocument document)
    {
        if (document.Version != 1 || document.Tasks is null || document.Tasks.Count > MaximumTasks) throw new ArgumentException("定时消息库版本或数量无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in document.Tasks)
        {
            if (task is null || task.Definition is null) throw new ArgumentException("定时消息条目无效。");
            ValidateId(task.Id); ValidateDraft(task.Definition);
            if (!ids.Add(task.Id) || task.Revision <= 0 || task.CreatedAt == default || task.UpdatedAt < task.CreatedAt || task.EvaluatedThrough < task.CreatedAt)
                throw new ArgumentException("定时消息标识、版本或时间无效。");
            if (task.LastRun is { } run)
            {
                if (run.OccurrenceId != OccurrenceId(task.Id, task.Revision, run.DueAt) || run.DueAt == default ||
                    run.DueAt > task.EvaluatedThrough || run.ClaimedAt == default || run.ClaimedAt < run.DueAt ||
                    (run.Status == "claimed" ? run.CompletedAt is not null || run.MessageId is not null :
                        !CompletionStatuses.Contains(run.Status) || run.CompletedAt is null || run.CompletedAt < run.ClaimedAt) ||
                    (run.Status == "api_accepted" ? !long.TryParse(run.MessageId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var messageId) || messageId == 0 : run.MessageId is not null))
                    throw new ArgumentException("定时消息执行元数据无效。");
            }
        }
    }

    private static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "D", out var value) || value == Guid.Empty || id != value.ToString("D"))
            throw new ArgumentException("任务ID须为非空小写UUID。");
    }

    private static void CheckProperties(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!remaining.Remove(property.Name)) throw new JsonException();
        if (remaining.Count != 0) throw new JsonException();
    }

    private static void EnsureNoLinks(string file)
    {
        for (var current = Path.GetFullPath(file); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("定时消息库不允许符号链接或目录联接路径。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed record ScheduleDocument(int Version, List<ScheduledMessageTask> Tasks);
}
