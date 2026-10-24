using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace AudioBridge;

public static class BotRuntime
{
    public static async Task RunAsync(BridgeConfig audio, BotSettings settings, string group, string mode,
        double seconds, CancellationToken ct)
    {
        if (mode is not ("listen" or "fixed" or "deepseek")) throw new ArgumentException("mode 为 listen、fixed 或 deepseek。");
        if (!double.IsFinite(seconds) || seconds is < 5 or > 300) throw new ArgumentException("bot seconds 为5～300秒。");
        if (string.IsNullOrWhiteSpace(group)) throw new ArgumentException("请提供已同意的测试群。");
        if (QqProcessTree.ListRoots().Count != 1)
            throw new InvalidOperationException("本机已验证的QQ应用输入偏好会影响同路径实例；机器人模式请只保留目标这一套桌面QQ。");
        var project = Environment.CurrentDirectory;
        // One bot owns the model, output and API ledger for this project. Diagnostic commands remain separate.
        var lockPath = Path.Combine(project, "Results", "local", "bot-session.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using var sessionLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var budget = new ApiBudget(Path.Combine(project, "Results", "local", "deepseek-budget.json"), settings.ApiBudgetYuan, settings.MaxApiCalls);
        var key = mode == "deepseek" ? BotSettings.LoadApiKey(project) : null;
        if (mode == "deepseek" && string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("未配置 DEEPSEEK_API_KEY；请填本地.env。可以先使用mode fixed或listen。");
        var inbox = settings.ImageEventsEnabled ? new GroupImageInbox(group, settings.OneBotSelfId, settings.OneBotPort) : null;
        if (inbox is not null)
        {
            var imageToken = BotSettings.LoadSecret(project, "ONEBOT_ACCESS_TOKEN");
            if (imageToken is not null) Environment.SetEnvironmentVariable("ONEBOT_ACCESS_TOKEN", imageToken, EnvironmentVariableTarget.Process);
        }
        using var images = inbox is null ? null : new ImageQuestionContext(inbox, group);
        using var cloud = mode == "deepseek" ? new DeepSeekReply(key!, budget, images: images) : null;
        using var recognizer = new LocalRecognizer(Path.GetFullPath(settings.ModelDirectory));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromSeconds(seconds));
        await using var speaker = new WindowsVoiceSpeaker(audio.OutputDeviceId!, audio.Volume, settings.SpeechRate, settings.Voice);
        await using var conversation = new VoiceConversation(cloud is null ? new FixedReply(settings.FixedReply) : cloud, speaker);
        var imageEvents = inbox?.StartAsync(lifetime.Token) ?? Task.CompletedTask;
        var frames = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(100) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        long sequence = 0, dropped = 0, requestSpeechEnd = 0;
        var meter = new SignalMeter();
        speaker.PlaybackStarted += () =>
        {
            var ended = Interlocked.Exchange(ref requestSpeechEnd, 0);
            if (ended != 0) Status("local_first_pcm", new { milliseconds = Stopwatch.GetElapsedTime(ended).TotalMilliseconds,
                scope = "last energy-active input packet to first nonzero PCM read by local player; excludes QQ/far-end buffers" });
        };
        var capture = Task.Run(async () =>
        {
            try
            {
                await NativeAudio.CaptureProcessAsync(audio.QqProcessId, (bytes, format) =>
                {
                    if (lifetime.IsCancellationRequested) return;
                    meter.Add(bytes.Span, format);
                    var frame = new AudioFrame(AudioFrames.ToMono(bytes.Span, format), format.SampleRate,
                        Interlocked.Increment(ref sequence), Stopwatch.GetTimestamp());
                    if (!frames.Writer.TryWrite(frame)) Interlocked.Increment(ref dropped);
                }, lifetime.Token).ConfigureAwait(false);
                frames.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { frames.Writer.TryComplete(); }
            catch (Exception error) { frames.Writer.TryComplete(error); lifetime.Cancel(); throw; }
        });
        var consumer = Task.Run(async () =>
        {
            long previous = 0, lastVoice = 0, voiceStart = 0;
            var sampleRate = 0;
            double voicedSeconds = 0;
            var triggered = false;
            try
            {
                await foreach (var frame in frames.Reader.ReadAllAsync(lifetime.Token))
                {
                    if ((previous != 0 && frame.Sequence != previous + 1) ||
                        (sampleRate != 0 && sampleRate != frame.SampleRate) || Stopwatch.GetElapsedTime(frame.ReceivedTimestamp).TotalSeconds > 1)
                    {
                        while (frames.Reader.TryRead(out _)) { }
                        recognizer.Reset();
                        await conversation.InterruptAsync();
                        voicedSeconds = 0; triggered = false; previous = 0; sampleRate = 0;
                        Status("input_reset", new { reason = "format change or old/dropped audio; ask again", dropped = Interlocked.Read(ref dropped) });
                        continue;
                    }
                    previous = frame.Sequence; sampleRate = frame.SampleRate;
                    if (frame.Rms >= .008)
                    {
                        if (voicedSeconds == 0 || lastVoice != 0 && Stopwatch.GetElapsedTime(lastVoice).TotalSeconds > 1.2)
                        { voicedSeconds = 0; voiceStart = frame.ReceivedTimestamp; }
                        voicedSeconds += frame.Samples.Length / (double)frame.SampleRate;
                        lastVoice = frame.ReceivedTimestamp;
                    }
                    foreach (var update in recognizer.Accept(frame.Samples, frame.SampleRate))
                    {
                        lifetime.Token.ThrowIfCancellationRequested();
                        var text = Normalize(update.Text);
                        if (settings.LogTranscripts) Status(update.IsFinal ? "asr_final" : "asr_partial", new { text });
                        var wake = text.Contains(settings.Name, StringComparison.Ordinal) || text.Contains("机器人", StringComparison.Ordinal);
                        var stop = text.Contains("停一下", StringComparison.Ordinal) || text.Contains("停止", StringComparison.Ordinal) || text.Contains("别说了", StringComparison.Ordinal);
                        var state = conversation.Snapshot();
                        if (voicedSeconds >= .25 && (wake || stop && (state.Speaking || state.Generating)) && !triggered)
                        {
                            var watch = Stopwatch.StartNew();
                            await conversation.InterruptAsync();
                            triggered = true;
                            Status("interrupt", new { localCleanupMilliseconds = watch.Elapsed.TotalMilliseconds,
                                fromVoiceOnsetToLocalCleanupMilliseconds = voiceStart == 0 ? (double?)null : Stopwatch.GetElapsedTime(voiceStart).TotalMilliseconds,
                                wasSpeaking = state.Speaking, wasGenerating = state.Generating,
                                scope = "input energy onset/recognized explicit trigger to local Speak cleanup; excludes QQ/far-end buffering" });
                        }
                        if (!update.IsFinal) continue;
                        if (voicedSeconds >= .25 && (triggered || wake) && !stop && mode != "listen")
                        {
                            var question = text.Replace(settings.Name, "", StringComparison.Ordinal).Replace("机器人", "", StringComparison.Ordinal).Trim();
                            if (question.Length == 0) question = "你好";
                            lifetime.Token.ThrowIfCancellationRequested();
                            Interlocked.Exchange(ref requestSpeechEnd, lastVoice);
                            var id = await conversation.SubmitAsync(question);
                            Status("question_accepted", new { generation = id, characters = question.Length, mode });
                        }
                        else if (update.Text.Length > 0) Status("utterance_ignored", new { characters = update.Text.Length, explicitTriggerRequired = true });
                        voicedSeconds = 0; triggered = false;
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch { lifetime.Cancel(); throw; }
        });
        Status("started", new { group, mode, durationSeconds = seconds, wake = new[] { settings.Name, "机器人" },
            stop = "停一下/停止/别说了，或先喊名字再提新问题", transcriptsLogged = settings.LogTranscripts,
            voice = settings.Voice, controls = "M静音，X停止，S状态，Q退出；Ctrl+C退出。无逐人身份。" });
        try
        {
            var nextStatus = DateTimeOffset.Now;
            while (!lifetime.IsCancellationRequested)
            {
                if (capture.IsFaulted) await capture;
                if (consumer.IsFaulted) await consumer;
                if (!Console.IsInputRedirected && Console.KeyAvailable)
                {
                    switch (Console.ReadKey(true).Key)
                    {
                        case ConsoleKey.Q: lifetime.Cancel(); break;
                        case ConsoleKey.X: await conversation.InterruptAsync(); break;
                        case ConsoleKey.M: await conversation.SetMutedAsync(!conversation.Snapshot().Muted); break;
                        case ConsoleKey.S: nextStatus = DateTimeOffset.MinValue; break;
                    }
                }
                if (DateTimeOffset.Now >= nextStatus)
                {
                    var state = conversation.Snapshot();
                    Status("status", new { state.Generation, state.Generating, state.Speaking, state.Muted, state.PendingSentences,
                        state.LastError, dropped = Interlocked.Read(ref dropped), input = meter.Snapshot(), budget = budget.Snapshot(),
                        images = inbox is null ? null : new { inbox.IsRunning, inbox.Count, inbox.LastError } });
                    nextStatus = DateTimeOffset.Now.AddSeconds(10);
                }
                await Task.Delay(25, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            // Close the admission gate before joining a consumer that may already be in an update.
            // Concurrent SubmitAsync either precedes this mute and is cancelled, or is rejected as muted.
            var muteAndStop = conversation.SetMutedAsync(true);
            // Both tasks are always observed. A native timeout is surfaced, never relabelled clean shutdown.
            try { await Task.WhenAll(capture, consumer, muteAndStop, imageEvents); }
            finally
            {
                try { await conversation.DisposeAsync(); }
                finally { await speaker.DisposeAsync(); }
            }
            Status("stopped", new { cleanupConfirmed = true, rawAudioSaved = false });
        }
    }

    public static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)));
    private static void Status(string status, object details) => Console.WriteLine(JsonSerializer.Serialize(
        new { command = "bot", status, local = DateTimeOffset.Now, details }, BridgeConfig.JsonOptions));
}
