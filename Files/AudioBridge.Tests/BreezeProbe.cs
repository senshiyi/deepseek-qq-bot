using System.Diagnostics;
using System.Text.Json;
using AudioBridge;

/// <summary>真实服务探针：用线上 voice.local.json 的配置请 Breeze 合成几种语气，落盘供试听，可选发一条 QQ 语音。</summary>
public static class BreezeProbe
{
    public static async Task<int> RunAsync(long voiceUser = 0)
    {
        var project = Environment.CurrentDirectory;
        var settings = VoiceSettings.Load(Path.Combine(project, "voice.local.json"));
        Console.WriteLine("provider=" + settings.TtsProvider + " base=" + settings.BreezeBaseUrl +
            " voiceId=" + (settings.BreezeVoiceId.Length == 0 ? "(design)" : settings.BreezeVoiceId) +
            " format=" + settings.BreezeResponseFormat + " timeout=" + settings.BreezeTimeoutSeconds);
        using var engine = new SpeechEngine(settings, null,
            (status, details) => Console.WriteLine(status + " " + JsonSerializer.Serialize(details, BridgeConfig.JsonOptions)));
        var directory = Path.Combine(project, "Results", "local");
        Directory.CreateDirectory(directory);
        byte[]? happy = null;
        foreach (var emotion in new[] { "normal", "happy", "shy", "angry", "sad" })
        {
            var watch = Stopwatch.StartNew();
            var wav = await engine.SynthesizeAsync(new VoiceCue("今天也想和你说说话，你最近过得好吗？", emotion, 0, false), CancellationToken.None);
            watch.Stop();
            if (emotion == "happy") happy = wav;
            var path = Path.Combine(directory, "breeze-probe-" + emotion + ".wav");
            await File.WriteAllBytesAsync(path, wav);
            Console.WriteLine("emotion=" + emotion + " bytes=" + wav.Length + " ms=" + watch.ElapsedMilliseconds +
                " seconds=" + ((wav.Length - 44) / 32000d).ToString("F2") + " file=" + path);
        }
        if (voiceUser <= 0 || happy is null) return 0;
        var token = BotSettings.LoadSecret(project, "ONEBOT_ACCESS_TOKEN") ?? throw new InvalidOperationException("缺少 ONEBOT_ACCESS_TOKEN。");
        using var client = new OneBotMessageClient("http://127.0.0.1:3000/", token, ["1095601728"],
            allowedPrivateUsers: [voiceUser.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        var receipt = await client.SendPrivateAsync(
            new(voiceUser.ToString(System.Globalization.CultureInfo.InvariantCulture), [OneBotSegment.Record(happy)]), CancellationToken.None);
        Console.WriteLine("voice sent message_id=" + receipt.MessageId + " bytes=" + happy.Length);
        return 0;
    }
}
