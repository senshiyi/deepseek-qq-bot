using System.Runtime.CompilerServices;
using AudioBridge;

public static class ConversationChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("first sentence speaks before generation ends", StreamingFirstSentence),
            ("replacement waits for old speaker and clears queued sentences", Replacement),
            ("late ignored cancellation cannot speak or change history", LateGeneration),
            ("synchronous provider startup cannot block interrupt", BlockingProviderStart),
            ("network cancellation callback cannot block interrupt", BlockingNetworkCancellation),
            ("history commits only fully completed speech and annotates interruption", PartialHistory),
            ("mute clears output and unmute never resumes old work", Mute),
            ("history and replies remain bounded", Bounds),
            ("dispose abandons network and fences late output", Dispose),
            ("dispose waits for active speaker cleanup", DisposeSpeaking),
            ("speaker cleanup timeout faults controls and permanently fences new speech", CleanupTimeout),
            ("provider and speaker failures are visible and recoverable", Failures)
        };
        foreach (var check in checks)
        {
            await check.Run().WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"PASS conversation: {check.Name}");
        }
        return checks.Length;
    }

    private static TaskCompletionSource Ready() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Reply Provider(Func<IReadOnlyList<ChatTurn>, CancellationToken, IAsyncEnumerable<string>> run) => new(run);
    private static async IAsyncEnumerable<string> Words(string text)
    {
        await Task.Yield();
        yield return text;
    }

    private static async Task StreamingFirstSentence()
    {
        var continueGeneration = Ready(); var entered = Ready();
        async IAsyncEnumerable<string> Stream()
        {
            yield return "第一句。";
            await continueGeneration.Task;
            yield return "第二句。";
        }
        var speaker = new Speaker((_, _) => { entered.TrySetResult(); return Task.CompletedTask; });
        await using var conversation = new VoiceConversation(Provider((_, _) => Stream()), speaker);
        await conversation.SubmitAsync("问题");
        await entered.Task;
        Assert(!continueGeneration.Task.IsCompleted && conversation.Snapshot().Generating,
            "speech waited for full provider response");
        continueGeneration.SetResult();
        await conversation.WaitForIdleAsync();
        Assert(speaker.Spoken.SequenceEqual(new[] { "第一句。", "第二句。" }), "stream order changed");
    }

    private static async Task Replacement()
    {
        var entered = Ready(); var cancelled = Ready(); var release = Ready(); var newEntered = Ready();
        var speaker = new Speaker(async (text, ct) =>
        {
            if (text == "旧一。")
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { cancelled.TrySetResult(); await release.Task; }
            }
            else if (text == "新句。") newEntered.TrySetResult();
        });
        await using var conversation = new VoiceConversation(Provider((history, _) =>
            Words(history.Last(t => t.Role == "user").Content == "旧" ? "旧一。旧二。旧三。" : "新句。")), speaker);
        var oldId = await conversation.SubmitAsync("旧");
        await entered.Task;
        var replacing = conversation.SubmitAsync("新");
        await cancelled.Task;
        Assert(!replacing.IsCompleted && !newEntered.Task.IsCompleted, "replacement did not wait for actual speech exit");
        release.SetResult();
        var newId = await replacing;
        await conversation.WaitForIdleAsync();
        Assert(newId > oldId && speaker.PeakOwners == 1 && newEntered.Task.IsCompleted, "speech owners overlapped");
        Assert(speaker.Spoken.SequenceEqual(new[] { "旧一。", "新句。" }), "cancelled queue spoke later");
    }

    private static async Task LateGeneration()
    {
        var releaseOld = Ready(); var oldExited = Ready(); var oldEntered = Ready();
        async IAsyncEnumerable<string> Old()
        {
            oldEntered.SetResult();
            try { await releaseOld.Task; yield return "迟到旧句。"; }
            finally { oldExited.TrySetResult(); }
        }
        var speaker = new Speaker((_, _) => Task.CompletedTask);
        await using var conversation = new VoiceConversation(Provider((history, _) =>
            history.Last(t => t.Role == "user").Content == "旧" ? Old() : Words("新句。")), speaker);
        await conversation.SubmitAsync("旧"); await oldEntered.Task;
        await conversation.SubmitAsync("新").WaitAsync(TimeSpan.FromSeconds(1));
        await conversation.WaitForIdleAsync();
        releaseOld.SetResult(); await oldExited.Task;
        Assert(speaker.Spoken.SequenceEqual(new[] { "新句。" }), "late old generation played");
        Assert(!conversation.Snapshot().History.Any(t => t.Content.Contains("迟到旧句")), "late text entered history");
    }

    private static async Task BlockingNetworkCancellation()
    {
        var providerEntered = Ready(); var callbackEntered = Ready();
        using var releaseCallback = new ManualResetEventSlim();
        var finishProvider = Ready();
        async IAsyncEnumerable<string> Stream([EnumeratorCancellation] CancellationToken ct)
        {
            using var registration = ct.Register(() => { callbackEntered.TrySetResult(); releaseCallback.Wait(); });
            providerEntered.SetResult();
            await finishProvider.Task;
            yield return "迟到。";
        }
        await using var conversation = new VoiceConversation(Provider((_, ct) => Stream(ct)), new Speaker((_, _) => Task.CompletedTask));
        await conversation.SubmitAsync("问题"); await providerEntered.Task;
        try
        {
            await conversation.InterruptAsync().WaitAsync(TimeSpan.FromSeconds(1));
            await callbackEntered.Task;
        }
        finally { releaseCallback.Set(); finishProvider.SetResult(); }
    }

    private static async Task BlockingProviderStart()
    {
        var entered = Ready(); var exited = Ready();
        using var release = new ManualResetEventSlim();
        async IAsyncEnumerable<string> Late()
        {
            await Task.Yield();
            try { yield return "迟到。"; }
            finally { exited.TrySetResult(); }
        }
        var speaker = new Speaker((_, _) => Task.CompletedTask);
        await using var conversation = new VoiceConversation(Provider((_, _) =>
        {
            entered.TrySetResult();
            release.Wait(); // Blocks before returning even the IAsyncEnumerable.
            return Late();
        }), speaker);
        await conversation.SubmitAsync("问题"); await entered.Task;
        try { await conversation.InterruptAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
        finally { release.Set(); }
        await exited.Task;
        Assert(speaker.Spoken.Count == 0, "late synchronous provider started speech");
    }

    private static async Task PartialHistory()
    {
        var secondEntered = Ready();
        var speaker = new Speaker(async (text, ct) =>
        {
            if (text == "未完句。") { secondEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
        });
        await using var conversation = new VoiceConversation(Provider((_, _) => Words("完整句。未完句。排队句。")), speaker);
        await conversation.SubmitAsync("问题"); await secondEntered.Task;
        await conversation.InterruptAsync();
        var snapshot = conversation.Snapshot();
        Assert(snapshot.History.Any(t => t.Role == "assistant" && t.Content == "完整句。"), "completed speech was lost");
        Assert(!snapshot.History.Any(t => t.Content.Contains("未完句") || t.Content.Contains("排队句")), "partial or queued speech was committed");
        Assert(snapshot.Interrupted && snapshot.History.Any(t => t.Role == "system" && t.Content.Contains("打断")), "interruption not annotated");
    }

    private static async Task Mute()
    {
        var entered = Ready();
        var speaker = new Speaker(async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); });
        await using var conversation = new VoiceConversation(Provider((_, _) => Words("一句。二句。")), speaker);
        await conversation.SubmitAsync("旧"); await entered.Task;
        await conversation.SetMutedAsync(true);
        var calls = speaker.Spoken.Count;
        await conversation.SubmitAsync("静音时的问题");
        await conversation.SetMutedAsync(false);
        await conversation.WaitForIdleAsync();
        Assert(speaker.Spoken.Count == calls && !conversation.Snapshot().Speaking &&
            !conversation.Snapshot().History.Any(t => t.Content.Contains("静音时的问题")), "unmute resumed old or muted work");
    }

    private static async Task Bounds()
    {
        var speaker = new Speaker((_, _) => Task.CompletedTask);
        await using var conversation = new VoiceConversation(Provider((_, _) => Words(new string('中', 1000))), speaker);
        for (var i = 0; i < 12; i++)
        {
            var start = speaker.Spoken.Count;
            await conversation.SubmitAsync($"问题{i}" + new string('文', 600));
            await conversation.WaitForIdleAsync();
            var sentences = speaker.Spoken.Skip(start).ToArray();
            Assert(sentences.Length <= 3 && sentences.Sum(t => t.Length) <= 180, "reply exceeded sentence or character bound");
            var history = conversation.Snapshot().History;
            Assert(history.Count(t => t.Role == "user") <= 8 && history.Sum(t => t.Content.Length) <= 4000, "history exceeded bound");
        }
        await conversation.SubmitAsync(new string('长', 10000)); await conversation.WaitForIdleAsync();
        Assert(conversation.Snapshot().History.Sum(t => t.Content.Length) <= 4000, "oversized input exceeded history bound");
    }

    private static async Task Dispose()
    {
        var entered = Ready(); var late = Ready(); var exited = Ready();
        async IAsyncEnumerable<string> Stream()
        {
            entered.SetResult();
            try { await late.Task; yield return "迟到。"; }
            finally { exited.TrySetResult(); }
        }
        var speaker = new Speaker((_, _) => Task.CompletedTask);
        var conversation = new VoiceConversation(Provider((_, _) => Stream()), speaker);
        await conversation.SubmitAsync("问题"); await entered.Task;
        await conversation.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await conversation.DisposeAsync();
        late.SetResult(); await exited.Task;
        Assert(conversation.Snapshot().Disposed && speaker.Spoken.Count == 0, "dispose allowed late output");
        try { await conversation.SubmitAsync("新"); throw new Exception("disposed controller accepted input"); }
        catch (ObjectDisposedException) { }
    }

    private static async Task Failures()
    {
        var failProvider = true;
        async IAsyncEnumerable<string> Stream()
        {
            await Task.Yield();
            if (failProvider) throw new IOException("provider unavailable");
            yield return "恢复。";
        }
        var failSpeaker = true;
        var speaker = new Speaker((_, _) => failSpeaker ? Task.FromException(new IOException("speaker unavailable")) : Task.CompletedTask);
        await using var conversation = new VoiceConversation(Provider((_, _) => Stream()), speaker);
        await conversation.SubmitAsync("一次"); await conversation.WaitForIdleAsync();
        Assert(conversation.Snapshot().LastError == "provider unavailable", "provider error was lost");
        failProvider = false;
        await conversation.SubmitAsync("二次"); await conversation.WaitForIdleAsync();
        Assert(conversation.Snapshot().LastError == "speaker unavailable", "speaker error was lost");
        failSpeaker = false;
        await conversation.SubmitAsync("三次"); await conversation.WaitForIdleAsync();
        Assert(conversation.Snapshot().LastError is null && conversation.Snapshot().History.Any(t => t.Content == "恢复。"), "retry did not recover");
    }

    private static async Task DisposeSpeaking()
    {
        var entered = Ready(); var cancelled = Ready(); var release = Ready();
        var speaker = new Speaker(async (_, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cancelled.TrySetResult(); await release.Task; }
        });
        var conversation = new VoiceConversation(Provider((_, _) => Words("正在播放。尚未播放。")), speaker);
        await conversation.SubmitAsync("问题"); await entered.Task;
        var disposing = conversation.DisposeAsync().AsTask();
        await cancelled.Task;
        Assert(!disposing.IsCompleted && conversation.Snapshot().Speaking, "dispose claimed cleanup before speaker exited");
        release.SetResult(); await disposing;
        Assert(!conversation.Snapshot().Speaking && speaker.Spoken.Count == 1,
            "dispose left output active or spoke the queued sentence");
        await conversation.DisposeAsync();
    }

    private static async Task CleanupTimeout()
    {
        const string failure = "fake output cleanup timed out while its owner remains active";
        async Task ExpectFailure(Task operation)
        {
            try { await operation.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException error) when (error.Message == failure || error.InnerException?.Message == failure) { return; }
            throw new Exception("cleanup timeout was reported as successful output cleanup");
        }
        foreach (var action in new[] { "interrupt", "mute", "dispose", "idle" })
        {
            var entered = Ready();
            var speaker = new Speaker(async (_, ct) =>
            {
                entered.TrySetResult();
                if (action != "idle")
                {
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                }
                throw new TimeoutException(failure);
            });
            var conversation = new VoiceConversation(Provider((_, _) => Words("正在发声。排队旧句。")), speaker);
            await conversation.SubmitAsync("旧问题");
            await entered.Task;
            await ExpectFailure(action switch
            {
                "interrupt" => conversation.InterruptAsync(),
                "mute" => conversation.SetMutedAsync(true),
                "dispose" => conversation.DisposeAsync().AsTask(),
                _ => conversation.WaitForIdleAsync()
            });
            Assert(conversation.Snapshot().LastError == failure, "fenced output cleanup failure was hidden");
            Assert(!conversation.Snapshot().History.Any(t => t.Role == "assistant"), "unconfirmed output entered history");
            await ExpectFailure(conversation.WaitForIdleAsync());
            if (action != "dispose")
            {
                await ExpectFailure(conversation.SubmitAsync("新问题"));
                await ExpectFailure(conversation.InterruptAsync());
                await ExpectFailure(conversation.SetMutedAsync(true));
                await ExpectFailure(conversation.SetMutedAsync(false));
            }
            else
            {
                try { await conversation.SubmitAsync("新问题"); throw new Exception("disposed output accepted replacement"); }
                catch (ObjectDisposedException) { }
            }
            Assert(speaker.Spoken.SequenceEqual(new[] { "正在发声。" }), "cleanup failure allowed queued or replacement speech");
            await ExpectFailure(conversation.DisposeAsync().AsTask());
            await ExpectFailure(conversation.DisposeAsync().AsTask());
        }
    }

    private sealed class Reply(Func<IReadOnlyList<ChatTurn>, CancellationToken, IAsyncEnumerable<string>> run) : IStreamingReply
    {
        public IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> history, CancellationToken ct) => run(history, ct);
    }

    private sealed class Speaker(Func<string, CancellationToken, Task> run) : IVoiceSpeaker
    {
        private readonly object gate = new();
        private readonly List<string> spoken = [];
        private int owners;
        public int PeakOwners { get; private set; }
        public IReadOnlyList<string> Spoken { get { lock (gate) return spoken.ToArray(); } }
        public async Task SpeakAsync(string text, CancellationToken ct)
        {
            lock (gate) { owners++; PeakOwners = Math.Max(PeakOwners, owners); spoken.Add(text); }
            try { await run(text, ct); }
            finally { lock (gate) owners--; }
        }
    }
}
