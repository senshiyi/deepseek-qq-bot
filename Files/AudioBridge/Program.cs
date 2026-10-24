using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace AudioBridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var command = args.FirstOrDefault() ?? "help";
            var options = ParseOptions(args.Skip(1).ToArray());
            var configPath = options.GetValueOrDefault("config", Path.Combine(Environment.CurrentDirectory, "config.local.json"));
            switch (command)
            {
                case "help": PrintHelp(); return 0;
                case "memes":
                case "meme-import":
                case "chat-send":
                case "chat-check":
                case "chat":
                case "chat-ai-check":
                case "school-check":
                case "school-ai-check":
                    using (var chatCancellation = new CancellationTokenSource())
                    {
                        ConsoleCancelEventHandler chatHandler = (_, e) => { e.Cancel = true; chatCancellation.Cancel(); };
                        Console.CancelKeyPress += chatHandler;
                        try { await GroupMessageCommands.RunAsync(command, options, chatCancellation.Token); }
                        finally { Console.CancelKeyPress -= chatHandler; }
                    }
                    return 0;
                case "devices": Write(NativeAudio.ListDevices()); return 0;
                case "qq": Write(QqProcessTree.ListRoots()); return 0;
                case "configure":
                    var config = File.Exists(configPath) ? BridgeConfig.Load(configPath) : new BridgeConfig();
                    config = config with
                    {
                        QqProcessId = options.TryGetValue("pid", out var pid) ? int.Parse(pid, CultureInfo.InvariantCulture) : config.QqProcessId,
                        OutputDeviceId = options.TryGetValue("output", out var output) ? output : config.OutputDeviceId,
                        Volume = options.TryGetValue("volume", out var volume) ? double.Parse(volume, CultureInfo.InvariantCulture) : config.Volume,
                        ToneMilliseconds = options.TryGetValue("tone-ms", out var ms) ? int.Parse(ms, CultureInfo.InvariantCulture) : config.ToneMilliseconds
                    };
                    ValidateRoutes(config);
                    config.Save(configPath);
                    Console.WriteLine($"已保存：{Path.GetFullPath(configPath)}。未采集或播放音频。");
                    return 0;
                case "check":
                    ValidateRoutes(BridgeConfig.Load(configPath));
                    Console.WriteLine("QQ 进程及输出设备可用。真实通话与 QQ 麦克风路由尚需人工验证。");
                    return 0;
                case "asr-file":
                    await AsrFileCommand.RunAsync(BotSettings.Load(options.GetValueOrDefault("bot-config", "bot.local.json")), Required(options, "wav"));
                    return 0;
                case "api-check":
                    await ApiCheckCommand.RunAsync(BotSettings.Load(options.GetValueOrDefault("bot-config", "bot.local.json")), CancellationToken.None);
                    return 0;
                case "images":
                    var imageSettings = BotSettings.Load(options.GetValueOrDefault("bot-config", "bot.local.json"));
                    var imageSeconds = options.TryGetValue("seconds", out var imageSecondsText) ? double.Parse(imageSecondsText, CultureInfo.InvariantCulture) : 60;
                    using (var imageCancellation = new CancellationTokenSource())
                    {
                        ConsoleCancelEventHandler imageHandler = (_, e) => { e.Cancel = true; imageCancellation.Cancel(); };
                        Console.CancelKeyPress += imageHandler;
                        try { await ImageRuntime.RunAsync(imageSettings, Required(options, "test-group"), imageSeconds, imageCancellation.Token); }
                        finally { Console.CancelKeyPress -= imageHandler; }
                    }
                    return 0;
                case "bot":
                    var botAudio = BridgeConfig.Load(configPath);
                    ValidateRoutes(botAudio);
                    var botSettings = BotSettings.Load(options.GetValueOrDefault("bot-config", "bot.local.json"));
                    var botSeconds = options.TryGetValue("seconds", out var botSecondsText) ? double.Parse(botSecondsText, CultureInfo.InvariantCulture) : 60;
                    using (var botCancellation = new CancellationTokenSource())
                    {
                        ConsoleCancelEventHandler botHandler = (_, e) => { e.Cancel = true; botCancellation.Cancel(); };
                        Console.CancelKeyPress += botHandler;
                        try { await BotRuntime.RunAsync(botAudio, botSettings, Required(options, "test-group"), options.GetValueOrDefault("mode", "fixed"), botSeconds, botCancellation.Token); }
                        finally { Console.CancelKeyPress -= botHandler; }
                    }
                    return 0;
                case "probe":
                case "tone":
                case "wav":
                    var diagnosticsConfig = BridgeConfig.Load(configPath);
                    ValidateRoutes(diagnosticsConfig);
                    var diagnosticsGroup = Required(options, "test-group");
                    if (string.IsNullOrWhiteSpace(diagnosticsGroup)) throw new ArgumentException("请写明已获得同意的测试群。");
                    using (var diagnosticsCancellation = new CancellationTokenSource())
                    {
                        ConsoleCancelEventHandler diagnosticsHandler = (_, e) => { e.Cancel = true; diagnosticsCancellation.Cancel(); };
                        Console.CancelKeyPress += diagnosticsHandler;
                        try
                        {
                            if (command == "tone") await DiagnosticsCommands.ToneAsync(diagnosticsConfig, diagnosticsGroup, diagnosticsCancellation.Token);
                            else if (command == "wav") await DiagnosticsCommands.WavAsync(diagnosticsConfig,
                                diagnosticsGroup, Required(options, "wav"), diagnosticsCancellation.Token);
                            else
                            {
                                var seconds = options.TryGetValue("seconds", out var secondsText) ? double.Parse(secondsText, CultureInfo.InvariantCulture) : 10;
                                double? toneAt = options.TryGetValue("tone-at", out var toneAtText) ? double.Parse(toneAtText, CultureInfo.InvariantCulture) : null;
                                await DiagnosticsCommands.ProbeAsync(diagnosticsConfig, diagnosticsGroup, seconds, toneAt, diagnosticsCancellation.Token);
                            }
                        }
                        finally { Console.CancelKeyPress -= diagnosticsHandler; }
                    }
                    return 0;
                case "run":
                    var runConfig = BridgeConfig.Load(configPath);
                    ValidateRoutes(runConfig);
                    var group = Required(options, "test-group");
                    if (string.IsNullOrWhiteSpace(group)) throw new ArgumentException("请写明已获得同意的测试群。");
                    var minutes = options.TryGetValue("minutes", out var duration) ? double.Parse(duration, CultureInfo.InvariantCulture) : 10;
                    if (!double.IsFinite(minutes) || minutes <= 0 || minutes > 30) throw new ArgumentException("minutes 必须在 0～30 分钟之间。");
                    await RunAsync(runConfig, group, TimeSpan.FromMinutes(minutes), options.GetValueOrDefault("wav"));
                    return 0;
                default: throw new ArgumentException($"未知命令：{command}。使用 help 查看帮助。");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"失败：{error.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var known = new HashSet<string> { "config", "pid", "output", "volume", "tone-ms", "test-group", "minutes", "wav", "seconds", "tone-at", "mode", "bot-config",
            "chat-config", "group", "text", "meme", "face", "live", "engine", "id", "file", "tags" };
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException("参数需为 --名称 值，例如 --volume 0.12。");
            var name = args[i][2..];
            if (!known.Contains(name) || !result.TryAdd(name, args[i + 1])) throw new ArgumentException($"未知或重复参数：{args[i]}");
        }
        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"缺少 --{name}。");

    private static void ValidateRoutes(BridgeConfig config)
    {
        config.Validate();
        using var process = Process.GetProcessById(config.QqProcessId);
        if (!string.Equals(process.ProcessName, "QQ", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("指定进程不是 QQ，请重新选择。");
        if (!QqProcessTree.ListRoots().Any(p => p.Pid == config.QqProcessId)) throw new ArgumentException("请选择 qq 命令列出的根进程，子进程不能覆盖整个 QQ 音频。");
        var device = NativeAudio.ListDevices().FirstOrDefault(d => d.Id == config.OutputDeviceId && d.Direction == "render");
        if (device is null) throw new ArgumentException("输出设备不可用，请重新执行 devices 和 configure。");
    }

    private static async Task RunAsync(BridgeConfig config, string group, TimeSpan duration, string? wavPath)
    {
        if (Console.IsInputRedirected) throw new ArgumentException("run 需要交互式终端；不会在后台自动开始收音或播放。");
        if (wavPath is not null && !File.Exists(wavPath)) throw new FileNotFoundException("找不到测试 WAV。", wavPath);
        using var lifetime = new CancellationTokenSource(duration);
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; lifetime.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        await using var playback = new PlaybackCoordinator(lifetime.Token);
        var meter = new SignalMeter();
        CancellationTokenSource? listening = null;
        Task listenTask = Task.CompletedTask;
        Exception? listenError = null;
        var captureCleanupUnconfirmed = false;

        async Task StopListening()
        {
            if (listening is null) return;
            await listening.CancelAsync();
            await listenTask;
            listening.Dispose();
            listening = null;
        }

        async Task StartListening()
        {
            if (captureCleanupUnconfirmed)
            {
                Console.Error.WriteLine("上次采集底层超时，资源未确认释放。本次不再重新采集，请退出本程序后检查 QQ 和音频设备。");
                return;
            }
            await StopListening();
            meter.Clear();
            listenError = null;
            listening = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var token = listening.Token;
            listenTask = Task.Run(async () =>
            {
                try { await NativeAudio.CaptureProcessAsync(config.QqProcessId, (data, format) => meter.Add(data.Span, format), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error)
                {
                    listenError = error;
                    if (error is TimeoutException) captureCleanupUnconfirmed = true;
                }
            });
        }

        Console.WriteLine($"测试群：{group}；本次最长 {duration.TotalMinutes:g} 分钟，到期自动关闭。请确认实际 QQ 窗口已在该群，参与者知情并戴耳机。");
        Console.WriteLine("尚未收音。L 开始/停止收听，P 播放固定测试音，W 播放指定 WAV，X 停止发声，M 静音/解除，S 状态，Q 退出。");
        Console.WriteLine("每次播放均需按键；不保存原始音频，不会把收到的音频回送到 QQ。");
        var nextMeter = DateTimeOffset.Now;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    switch (key)
                    {
                        case ConsoleKey.L:
                            if (listening is not null && !listenTask.IsCompleted) { await StopListening(); Console.WriteLine("收听已停止。"); }
                            else
                            {
                                await StartListening();
                                if (listening is not null) Console.WriteLine("开始采集指定 QQ 进程树；正在等待音频。");
                            }
                            break;
                        case ConsoleKey.P:
                            await playback.StartAsync((_, ct) => NativeAudio.PlayAsync(config.OutputDeviceId!, TestTone.Create(config.Volume, config.ToneMilliseconds), ct));
                            Console.WriteLine(playback.Muted ? "当前静音，未播放。" : "固定测试音已请求播放；让远端确认是否听到。");
                            break;
                        case ConsoleKey.W:
                            if (wavPath is null) { Console.WriteLine("未指定 --wav。可以先用 P 的固定测试音。"); break; }
                            await playback.StartAsync(async (_, ct) =>
                            {
                                using var reader = new WaveFileReader(wavPath);
                                // Match the configured gain, without changing the Windows endpoint volume.
                                var provider = new NAudio.Wave.SampleProviders.VolumeSampleProvider(reader.ToSampleProvider()) { Volume = (float)config.Volume };
                                await NativeAudio.PlayAsync(config.OutputDeviceId!, provider.ToWaveProvider(), ct);
                            });
                            break;
                        case ConsoleKey.X:
                            var watch = Stopwatch.StartNew();
                            await playback.StopAsync();
                            Console.WriteLine($"本地停止请求和播放器清理完成：{watch.Elapsed.TotalMilliseconds:F1}ms。此值不包含 QQ/远端缓冲延迟。");
                            break;
                        case ConsoleKey.M:
                            await playback.SetMutedAsync(!playback.Muted);
                            Console.WriteLine(playback.Muted ? "输出已静音并停止。仍可收听。" : "已解除静音；不会恢复旧声音。");
                            break;
                        case ConsoleKey.S:
                            Write(new { listening = listening is not null && !listenTask.IsCompleted, playback.Playing, playback.Muted, playback.Generation,
                                playbackError = playback.LastError?.Message, captureError = listenError?.Message, input = meter.Snapshot() });
                            break;
                        case ConsoleKey.Q: lifetime.Cancel(); break;
                    }
                }
                if (listening is not null && listenTask.IsCompleted)
                {
                    await StopListening();
                    Console.Error.WriteLine($"收听已停止：{listenError?.Message ?? "采集结束"}。" +
                        (captureCleanupUnconfirmed ? "请退出程序后检查设备，当前会话禁止重开采集。" : "修复后按 L 重试。"));
                }
                if (DateTimeOffset.Now >= nextMeter)
                {
                    if (listening is not null) Write(meter.Snapshot());
                    if (playback.LastError is not null) Console.Error.WriteLine($"播放失败：{playback.LastError.Message}；可检查设备后重试。");
                    nextMeter = DateTimeOffset.Now.AddSeconds(2);
                }
                try { await Task.Delay(25, lifetime.Token); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            await playback.StopAsync();
            await StopListening();
            Console.CancelKeyPress -= cancelHandler;
            Console.WriteLine(captureCleanupUnconfirmed
                ? "已停止应用侧收发；底层采集曾超时，资源未确认释放，请退出本程序并检查设备。"
                : "已停止采集和播放，音频资源已释放。");
        }
    }

    private static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value, BridgeConfig.JsonOptions));

    private static void PrintHelp() => Console.WriteLine("""
        QQ 群消息与本地语音工具（语音联调已暂停；真实群收发尚待账号登录验证）
        群消息功能（语音联调已按用户要求暂停）：
        memes                                        列表情包ID和标签，不发送
        meme-import --id "my-meme" --file "本地完整路径" --tags "开心,庆祝"
        chat-check                                   只读核对QQ在线状态及OneBot登录账号，不发消息
        chat-send --group "白名单群" --text "正文" [--meme roger] [--face 14]
                  [--live true]                      默认仅预览；明确live才真实发送
        chat --group "白名单群" [--seconds 600] [--mode mention|active|manual]
             [--live true] [--engine fixed|deepseek|school]  P暂停/恢复，X暂停，S状态，Q退出
             --seconds 0                            持续运行；Q/Ctrl+C退出，限频和累计预算不变
        chat-ai-check [--text "测试文字"]              正式AI JSON选择测试，不发群消息，计入0.30元
        school-check                                仅检查学校配置和独立点数账本，不联网
        school-ai-check [--text "测试文字"]           正式学校API测试，消耗学校点数，不发QQ
        群消息命令可用--chat-config，默认group-chat.local.json；不启动语音。
        devices                                      列出活动输入/输出设备，不收音
        qq                                           列出 QQ 进程候选，不收音
        configure --pid 123 --output "设备ID"          保存本地配置
                  [--volume 0.12] [--tone-ms 800]
        check                                        只读检查配置中的路由
        probe --test-group "已获同意的测试群"         有限收听电平探测，默认 10 秒
              [--seconds 10] [--tone-at 3]           仅指定 tone-at 时在该秒播放一次
        tone --test-group "已获同意的测试群"          单次测试音，等待本地播放结束
        wav --test-group "已获同意的测试群" --wav "固定回复.wav"
                                                     单次 PCM/IEEE WAV，最长 30 秒，使用配置音量
        asr-file --wav "测试句.wav"                   本地文件识别；不采集、不播放、不上传
        api-check                                    正式API小额文字连通测试，计入0.30元累计预算
        images --test-group "已同意群" [--seconds 60]  独立本机OneBot图片事件入口，需Token与真实NapCat部署
        bot --test-group "已获同意的测试群"           立即收听，默认60秒；M静音/X停止/S状态/Q退出
            [--mode fixed|listen|deepseek] [--seconds 60] [--bot-config "bot.local.json"]
                                                     fixed本地固定回复；deepseek需密钥，0.30元累计上限
        唤醒先用“机器人”；停止词“停一下/停止/别说了”。保留多轮上下文，每个问题明确唤醒。
        run --test-group "已获同意的测试群"           进入手动联调；默认 10 分钟
            [--minutes 10] [--wav "固定回复.wav"]
        所有命令可用 --config "配置路径"。QQ 重启后应重新选择进程。
        run 不会自动采集/播放，按 L/P 开始；M 静音，X 停止，S 状态，Q 退出。
        probe/tone/wav 会立即开始各自的测试，只能在已确认的目标群通话中执行。
        """);
}
