using System.Text;
using AudioBridge;
using NAudio.Wave;

/// <summary>真实网络/真实语音链路的显式探针：默认不运行，只有管理员手动带参数才执行。</summary>
public static class SelfServiceProbe
{
    public static async Task<int> WebAsync()
    {
        using var web = new PublicWeb((status, details) => Console.WriteLine(status + " " + System.Text.Json.JsonSerializer.Serialize(details)));
        var page = await web.ReadPageAsync("https://example.com/", CancellationToken.None);
        Console.WriteLine("page host=" + new Uri(page.Url).Host + " title=" + page.Title + " characters=" + page.Text.Length);
        Console.WriteLine("page text head=" + page.Text[..Math.Min(200, page.Text.Length)].Replace('\n', ' '));
        foreach (var url in new[] { "http://127.0.0.1:17802/api/status", "http://192.168.1.1/", "https://example.com/" })
        {
            try { using var probe = new PublicWeb(); _ = await probe.ReadPageAsync(url, CancellationToken.None); Console.WriteLine("ACCEPTED " + url); }
            catch (Exception error) { Console.WriteLine("refused " + url + " -> " + error.GetType().Name); }
        }
        var image = await web.DownloadImageAsync("https://www.baidu.com/img/flexible/logo/pc/result.png", CancellationToken.None);
        Console.WriteLine("image bytes=" + image.Length + " magic=" + Convert.ToHexString(image.AsSpan(0, Math.Min(4, image.Length))));
        return 0;
    }

    public static async Task<int> VoiceRoundTripAsync()
    {
        var settings = new VoiceSettings();
        var wav = await QqVoice.SpeechWavAsync("今天天气不错，我们出去走走吧。", settings, CancellationToken.None);
        var path = Path.Combine(Path.GetTempPath(), "voice-roundtrip-" + Guid.NewGuid().ToString("N") + ".wav");
        await File.WriteAllBytesAsync(path, wav);
        Console.WriteLine("wav bytes=" + wav.Length + " seconds=" + (wav.Length - 44) / 32000d);
        using var reader = new WaveFileReader(path);
        using var recognizer = new LocalRecognizer(Path.GetFullPath("Models/sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23"));
        var text = new StringBuilder();
        var format = reader.WaveFormat;
        var buffer = new byte[Math.Max(format.BlockAlign, format.AverageBytesPerSecond / 50 / format.BlockAlign * format.BlockAlign)];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            foreach (var update in recognizer.Accept(AudioFrames.ToMono(buffer.AsSpan(0, read), format), format.SampleRate))
                if (update.IsFinal) text.Append(update.Text);
        for (var i = 0; i < 80; i++)
            foreach (var update in recognizer.Accept(new float[format.SampleRate / 50], format.SampleRate))
                if (update.IsFinal) text.Append(update.Text);
        foreach (var update in recognizer.Flush()) if (update.IsFinal) text.Append(update.Text);
        Console.WriteLine("recognized=" + text.ToString());
        try { File.Delete(path); } catch (IOException) { }
        return text.ToString().Length >= 4 ? 0 : 1;
    }

    /// <summary>真的给白名单联系人发一条语音，用来确认真实 QQ 能收到 record 消息。</summary>
    public static async Task<int> VoiceSendAsync(string userId)
    {
        var project = Environment.CurrentDirectory;
        var token = BotSettings.LoadSecret(project, "ONEBOT_ACCESS_TOKEN") ?? throw new InvalidOperationException("缺少 ONEBOT_ACCESS_TOKEN。");
        using var client = new OneBotMessageClient("http://127.0.0.1:3000/", token, ["1095601728"], allowedPrivateUsers: [userId]);
        var wav = await QqVoice.SpeechWavAsync("这是一条语音测试，我是大肥鱼。", new VoiceSettings(), CancellationToken.None);
        var receipt = await client.SendPrivateAsync(new(userId, [OneBotSegment.Record(wav)]), CancellationToken.None);
        Console.WriteLine("voice sent message_id=" + receipt.MessageId + " bytes=" + wav.Length);
        return 0;
    }
}
