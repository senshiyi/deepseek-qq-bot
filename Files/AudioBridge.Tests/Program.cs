using AudioBridge;
using NAudio.Wave;

if (args is ["--runner-probe", var probeMode, var probePort])
    return await SuiteProcessChecks.ProbeAsync(probeMode, int.Parse(probePort, System.Globalization.CultureInfo.InvariantCulture));
if (args is ["--admin-preview"]) { await AdminHttpChecks.PreviewAsync(); return 0; }
if (args is ["--vision-probe"]) return await VisionProbe.RunAsync();
if (args is ["--vision-probe", var probeImage]) return await VisionProbe.RunAsync(probeImage);

var tests = new (string Name, Func<Task> Body)[]
{
    ("isolated suite timeout releases resources before the next suite", SuiteProcessChecks.RunAsync),
    ("replacement waits for cancelled player cleanup", Replacement),
    ("stop cancels output and dispose is idempotent", StopAndDispose),
    ("mute stops current output and never resumes old data", Mute),
    ("playback device failure is observed and next output can recover", Fault),
    ("simultaneous start requests retain one active device owner", ConcurrentStarts),
    ("session deadline cancels audio independently of the command loop", Lifetime),
    ("cancelled generation cannot enqueue late audio", StaleQueue),
    ("output queue stays bounded and preserves aligned PCM", QueueBounds),
    ("drained completed queue returns EOF", QueueDrain),
    ("fixed tone is bounded and has finite nonzero energy", Tone),
    ("signal meter distinguishes silence from PCM speech-like energy", Meter),
    ("invalid gain and duration are rejected", Config),
    ("probe rejects out-of-range duration before opening hardware", ProbeDuration),
    ("probe rejects unsafe scheduled tone positions before opening hardware", ProbeTonePosition),
    ("pre-cancelled diagnostics never open hardware", CancelledDiagnostics),
    ("invalid, compressed, empty and overlong WAVs never reach a player", WavDiagnosticsTests.InvalidWavs),
    ("IEEE WAV and the exact 30 second boundary are accepted", WavDiagnosticsTests.ValidFormatsAndBoundary),
    ("WAV applies configured gain and waits for its single player", WavDiagnosticsTests.GainAndCompletion),
    ("shared single playback cancellation waits for cleanup", WavDiagnosticsTests.Cancellation),
    ("shared single playback timeout cancels its player", WavDiagnosticsTests.Timeout),
    ("pre-cancelled single playback disposes its unsubmitted source", WavDiagnosticsTests.CancelledBeforeSubmission),
    ("DeepSeek streams only user-facing content with bounded nonthinking request", ApiChecks.BodyOnly),
    ("API conservative budget persists and refuses exhausted or invalid ledgers", ApiChecks.BudgetLimits),
    ("API cancellation reaches HTTP and retains uncertain spend reservation", ApiChecks.Cancellation),
    ("voice conversation concurrency and spoken-history scenarios", async () => { await ConversationChecks.RunAsync(); }),
    ("downmix handles float, PCM and malformed frame boundaries", AudioFrameChecks),
    ("cancelled speech provider cannot supply late PCM", WindowsVoiceChecks.CancellationStopsPcm),
    ("group image inbox scope, age and ambiguity scenarios", async () => { await ImageInboxChecks.RunAsync(); }),
    ("image questions reject unsafe download and require explicit disambiguation", async () => { await ImageQuestionChecks.RunAsync(); }),
    ("DeepSeek attaches bounded image only to the current user question", ApiChecks.ImagePayload),
    ("messaging: local OneBot sends and delivery uncertainty", async () => { await MessageClientChecks.RunAsync(); }),
    ("messaging: curated meme import and path boundaries", async () => { await MemeLibraryChecks.RunAsync(); }),
    ("messaging: selected received images and source boundaries", async () => { await ReceivedImageChecks.RunAsync(); }),
    ("messaging: durable scheduled message definitions and occurrences", async () => { await ScheduledMessageChecks.RunAsync(); }),
    ("messaging: scheduled delivery and shared worker lifecycle", async () => { await ScheduledRuntimeIntegrationChecks.RunAsync(); }),
    ("messaging: group triggers, deduplication and limits", async () => { await GroupChatPolicyChecks.RunAsync(); }),
    ("messaging: private contacts, isolation and late-result cancellation", async () => { await PrivateChatChecks.RunAsync(); }),
    ("messaging: reply cancellation and loopback integration", async () => { await GroupChatFlowChecks.RunAsync(); }),
    ("messaging: school model transport and independent points budget", async () => { await SchoolApiChecks.RunAsync(); }),
    ("admin: profile and private notes storage", async () => { await AdminProfileChecks.RunAsync(); }),
    ("admin: private history retention and approved public preferences", async () => { await ContactContextChecks.RunAsync(); }),
    ("admin: explicit private memory requests require review", async () => { await MemorySuggestionChecks.RunAsync(); }),
    ("admin: companion mood, diary forgetting and wake gate", async () => { await CompanionChecks.RunAsync(); }),
    ("admin: observable background lifecycle and model failures", BackgroundRuntimeChecks.RunAsync),
    ("admin: bounded history-to-persona drafts and cancellation", async () => { await PersonaLearningChecks.RunAsync(); }),
    ("admin: persona isolation and bounded model output", async () => { await AdminReplyChecks.RunAsync(); }),
    ("admin: loopback HTTP and application lifecycle", async () => { await AdminHttpChecks.RunAsync(); }),
    ("admin: QQ favorites selections and bounded downloads", async () => { await QqEmojiChecks.RunAsync(); })
};
if (args is ["--case", var caseText] && int.TryParse(caseText, out var caseIndex) && caseIndex >= 0 && caseIndex < tests.Length)
{
    try { await tests[caseIndex].Body(); return 0; }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
var selectedTests = args.Length == 2 && args[0] == "--suite" && args[1] is "messaging" or "admin"
    ? tests.Select((test, index) => (test.Name, Index: index)).Where(t => t.Name.StartsWith(args[1] + ":", StringComparison.Ordinal)).ToArray()
    : args.Length == 0 ? tests.Select((test, index) => (test.Name, Index: index)).ToArray()
    : throw new ArgumentException("支持--suite messaging / admin，或不传参数运行全部无硬件检查。");
var failed = 0;
foreach (var test in selectedTests)
{
    // Each suite owns a process. A timeout must release its ports and workers before the next suite starts.
    var result = await SuiteProcessRunner.RunAsync(["--case", test.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(90));
    Console.Write(result.Output);
    Console.Error.Write(result.Error);
    if (result.ExitCode == 0 && !result.TimedOut) Console.WriteLine($"PASS {test.Name}");
    else { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {(result.TimedOut ? "suite exceeded 90 seconds; child process exited before continuing" : $"exit {result.ExitCode}")}"); }
}
Console.WriteLine($"{selectedTests.Length - failed}/{selectedTests.Length} suites passed. No audio hardware opened; no real QQ messages sent.");
return failed == 0 ? 0 : 1;

static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
static TaskCompletionSource Ready() => new(TaskCreationOptions.RunContinuationsAsynchronously);

static Task AudioFrameChecks()
{
    var input = new float[] { 1, -1, .5f, .5f, float.NaN, .25f };
    var bytes = new byte[input.Length * 4];
    Buffer.BlockCopy(input, 0, bytes, 0, bytes.Length);
    var result = AudioFrames.ToMono(bytes, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
    Assert(result.SequenceEqual(new float[] { 0, .5f, .125f }), "invalid float downmix");
    var pcm = AudioFrames.ToMono(new byte[] { 0, 64, 0, 192 }, new WaveFormat(16000, 16, 1));
    Assert(pcm.SequenceEqual(new float[] { .5f, -.5f }), "invalid PCM downmix");
    try { AudioFrames.ToMono(new byte[3], WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)); }
    catch (NotSupportedException) { return Task.CompletedTask; }
    throw new Exception("misaligned audio accepted");
}

static async Task Replacement()
{
    await using var controller = new PlaybackCoordinator();
    var entered = Ready(); var cancelled = Ready(); var release = Ready(); var secondEntered = Ready();
    await controller.StartAsync(async (_, ct) =>
    {
        entered.SetResult();
        try { await Task.Delay(Timeout.Infinite, ct); }
        finally { cancelled.SetResult(); await release.Task; }
    });
    await entered.Task;
    var previousId = controller.Generation;
    var replacing = controller.StartAsync((_, _) => { secondEntered.SetResult(); return Task.CompletedTask; });
    await cancelled.Task;
    Assert(!secondEntered.Task.IsCompleted, "new player entered before old player released device");
    Assert(!replacing.IsCompleted, "replacement did not wait for cleanup");
    release.SetResult();
    await replacing;
    await secondEntered.Task;
    Assert(controller.Generation > previousId, "generation was not invalidated");
}

static async Task StopAndDispose()
{
    var controller = new PlaybackCoordinator();
    var entered = Ready(); var exited = Ready();
    await controller.StartAsync(async (_, ct) =>
    {
        entered.SetResult();
        try { await Task.Delay(Timeout.Infinite, ct); }
        finally { exited.SetResult(); }
    });
    await entered.Task;
    await controller.StopAsync();
    Assert(exited.Task.IsCompleted && !controller.Playing, "stop returned before output finished");
    await controller.DisposeAsync(); await controller.DisposeAsync();
    try { await controller.StartAsync((_, _) => Task.CompletedTask); throw new Exception("disposed controller accepted output"); }
    catch (ObjectDisposedException) { }
}

static async Task Mute()
{
    await using var controller = new PlaybackCoordinator();
    var entered = Ready(); var starts = 0;
    await controller.StartAsync(async (_, ct) => { Interlocked.Increment(ref starts); entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
    await entered.Task;
    await controller.SetMutedAsync(true);
    await controller.StartAsync((_, _) => { Interlocked.Increment(ref starts); return Task.CompletedTask; });
    await controller.SetMutedAsync(false);
    Assert(starts == 1 && !controller.Playing, "unmute resumed or mute accepted audio");
    await controller.StartAsync((_, _) => { Interlocked.Increment(ref starts); return Task.CompletedTask; });
    Assert(starts == 2, "explicit output after unmute failed");
}

static async Task Fault()
{
    await using var controller = new PlaybackCoordinator();
    await controller.StartAsync((_, _) => Task.FromException(new IOException("synthetic device removed")));
    Assert(controller.LastError is IOException && !controller.Playing, "fault was lost or left player active");
    var retried = false;
    await controller.StartAsync((_, _) => { retried = true; return Task.CompletedTask; });
    Assert(retried && controller.LastError is null, "device retry failed");
}

static async Task ConcurrentStarts()
{
    await using var controller = new PlaybackCoordinator();
    var active = 0; var peak = 0;
    async Task Player(long _, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref active);
        peak = Math.Max(peak, now);
        try { await Task.Delay(Timeout.Infinite, ct); }
        finally { await Task.Yield(); Interlocked.Decrement(ref active); }
    }
    await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => Task.Run(() => controller.StartAsync(Player))));
    await controller.StopAsync();
    Assert(peak == 1 && active == 0, "concurrent output sessions overlapped or leaked");
}

static Task StaleQueue()
{
    var queue = new PcmQueue(new WaveFormat(16000, 16, 1));
    var oldId = queue.Reset();
    Assert(queue.TryEnqueue(oldId, new byte[] { 1, 2, 3, 4 }), "initial enqueue failed");
    var newId = queue.Reset();
    Assert(queue.BufferedBytes == 0, "cancel left queued audio");
    Assert(!queue.TryEnqueue(oldId, new byte[] { 9, 9 }), "late old audio was accepted");
    queue.Complete(oldId);
    Assert(queue.TryEnqueue(newId, new byte[] { 5, 6 }), "old completion closed new stream");
    queue.Complete(newId);
    var output = new byte[8];
    Assert(queue.Read(output) == 2 && output[0] == 5 && output[1] == 6, "old audio leaked into new stream");
    return Task.CompletedTask;
}

static async Task Lifetime()
{
    using var lifetime = new CancellationTokenSource();
    await using var controller = new PlaybackCoordinator(lifetime.Token);
    var entered = Ready(); var stopped = Ready();
    await controller.StartAsync(async (_, ct) =>
    {
        entered.SetResult();
        try { await Task.Delay(Timeout.Infinite, ct); }
        finally { stopped.SetResult(); }
    });
    await entered.Task;
    // No StopAsync or UI progress: this represents a command waiting on another device.
    await lifetime.CancelAsync();
    await stopped.Task;
    try { await controller.StartAsync((_, _) => Task.CompletedTask); throw new Exception("expired session accepted output"); }
    catch (OperationCanceledException) { }
}

static Task QueueBounds()
{
    var queue = new PcmQueue(new WaveFormat(16000, 16, 1), 20);
    var id = queue.Reset();
    Assert(queue.TryEnqueue(id, new byte[640]), "capacity boundary was rejected");
    Assert(!queue.TryEnqueue(id, new byte[2]) && queue.BufferedBytes == 640, "queue grew past capacity");
    Assert(queue.Read(new byte[321]) == 320 && queue.BufferedBytes == 320, "partial read broke PCM alignment");
    try { queue.TryEnqueue(id, new byte[1]); throw new Exception("unaligned frame accepted"); }
    catch (ArgumentException) { }
    return Task.CompletedTask;
}

static Task QueueDrain()
{
    var queue = new PcmQueue(new WaveFormat(16000, 16, 1));
    var id = queue.Reset();
    var bytes = new byte[] { 7, 1, 8, 2, 9, 3 };
    queue.TryEnqueue(id, bytes); queue.Complete(id);
    var first = new byte[4]; var second = new byte[4];
    Assert(queue.Read(first) == 4 && first.SequenceEqual(bytes[..4]), "FIFO order broke");
    Assert(queue.Read(second) == 2 && second[0] == 9 && second[1] == 3 && queue.Read(second) == 0, "final drain did not reach EOF");
    Assert(!queue.TryEnqueue(id, new byte[2]), "completed stream accepted late audio");
    return Task.CompletedTask;
}

static Task Tone()
{
    var source = TestTone.Create(0.12, 800);
    var bytes = new byte[96000];
    var count = source.Read(bytes);
    Assert(count == 76800 && source.Read(bytes) == 0, "fixed audio length changed");
    Assert(bytes.Take(count).Any(x => x != 0), "test tone is silent");
    return Task.CompletedTask;
}

static Task Meter()
{
    var meter = new SignalMeter();
    meter.Add(new byte[320], new WaveFormat(16000, 16, 1));
    var silent = System.Text.Json.JsonSerializer.Serialize(meter.Snapshot());
    Assert(silent.Contains("\"peak\":0") && silent.Contains("\"lastSignal\":null"), "silence treated as signal");
    meter.Add(new byte[] { 0, 64, 0, 64 }, new WaveFormat(16000, 16, 1));
    var signal = System.Text.Json.JsonSerializer.Serialize(meter.Snapshot());
    Assert(signal.Contains("\"peak\":0.5") && !signal.Contains("\"lastSignal\":null"), "PCM energy was missed");
    return Task.CompletedTask;
}

static Task Config()
{
    foreach (var value in new[] { -0.1, double.NaN, double.PositiveInfinity, 0.51 })
    {
        try { new BridgeConfig { Volume = value }.Validate(false); throw new Exception("unsafe gain accepted"); }
        catch (ArgumentException) { }
    }
    try { new BridgeConfig { ToneMilliseconds = 10000 }.Validate(false); throw new Exception("unbounded tone accepted"); }
    catch (ArgumentException) { }
    return Task.CompletedTask;
}

static async Task ProbeDuration()
{
    foreach (var seconds in new[] { 0, 0.999, 30.01, double.NaN, double.PositiveInfinity })
    {
        try { await DiagnosticsCommands.ProbeAsync(new BridgeConfig(), "simulated", seconds, null, default); throw new Exception("invalid duration accepted"); }
        catch (ArgumentException) { }
    }
}

static async Task ProbeTonePosition()
{
    foreach (var at in new[] { -0.01, 9.21, double.NaN, double.PositiveInfinity })
    {
        try { await DiagnosticsCommands.ProbeAsync(new BridgeConfig(), "simulated", 10, at, default); throw new Exception("unsafe tone schedule accepted"); }
        catch (ArgumentException) { }
    }
}

static async Task CancelledDiagnostics()
{
    using var stopping = new CancellationTokenSource();
    stopping.Cancel();
    try { await DiagnosticsCommands.ProbeAsync(new BridgeConfig(), "simulated", 1, null, stopping.Token); throw new Exception("cancelled probe started"); }
    catch (OperationCanceledException) { }
    try { await DiagnosticsCommands.ToneAsync(new BridgeConfig(), "simulated", stopping.Token); throw new Exception("cancelled tone started"); }
    catch (OperationCanceledException) { }
}
