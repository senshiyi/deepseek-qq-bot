using System.Net;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioBridge;

/// <summary>
/// 八种语音语气。只用于语音合成指令，进不了发出去的文字：解析时单独存字段，正文从不带它。
/// </summary>
public static class VoiceEmotion
{
    public const string Normal = "normal";
    public static readonly string[] All = [Normal, "happy", "shy", "angry", "sad", "comfort", "excited", "serious"];

    public static bool IsSupported(string? emotion) => emotion is not null && Array.IndexOf(All, emotion) >= 0;

    /// <summary>只认小写已知标签；认不出的当作没给——一个坏标签不该毁掉整句回复。</summary>
    public static string? Normalize(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length is < 1 or > 32) return null;
        text = text.ToLowerInvariant();
        return IsSupported(text) ? text : null;
    }

    /// <summary>音色始终由 voice ID 决定，这里只改语气，免得情绪变化把声线带跑。</summary>
    public static string Instruction(string? emotion) => emotion switch
    {
        "happy" => "语气开心、兴奋，声音里带一点笑意。",
        "shy" => "语气害羞、轻柔，话说得有一点迟疑。",
        "angry" => "语气不满，带一点抱怨和傲娇，不是吼人。",
        "sad" => "语气低落、有点委屈，语速稍慢，声音放轻。",
        "comfort" => "语气温柔体贴，像在轻声安慰人。",
        "excited" => "语气激动、有活力，语速略快但不要喊。",
        "serious" => "语气认真沉稳，吐字清晰，不要玩笑腔。",
        _ => "语气自然、亲切、轻松，像平常聊天。"
    };
}

/// <summary>409 只表示服务正忙，换个时机再发就能成；其余失败才算真的坏了。</summary>
public sealed class BreezeBusyException() : Exception("Breeze 正在生成另一条语音。");

/// <summary>
/// 本地 Breeze TTS 2 的 HTTP 客户端（OpenAI 兼容 POST /v1/audio/speech）。
/// 服务只有一份模型、一次只生成一条，并发会回 409，所以这里串行发送；连续失败后短暂停用，
/// 免得每条消息都白等一次超时。返回值一律是 QQ 语音要求的 16kHz/16位/单声道 WAV。
/// </summary>
public sealed class BreezeTtsClient : IDisposable
{
    public const int DefaultSampleRate = 24000;
    private const int MaximumAudioBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);
    private static readonly string[] Formats = ["pcm", "wav", "mp3"];
    private readonly Uri endpoint;
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string? voiceId;
    private readonly string instructions;
    private readonly string format;
    private readonly TimeSpan timeout;
    private readonly Func<DateTimeOffset> clock;
    private readonly Action<string, object>? log;
    private DateTimeOffset skipUntil;
    private int disposed;

    public BreezeTtsClient(string baseUrl, string? voiceId, string instructions, string responseFormat, TimeSpan timeout,
        HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null, Action<string, object>? log = null)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || baseUrl.Length > 200)
            throw new ArgumentException("Breeze 地址必须是 http/https 根地址，不能带账号、查询或锚点。");
        var id = (voiceId ?? "").Trim();
        if (id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("Breeze 音色 ID 只能用字母、数字、下划线和连字符，最多 64 字符。");
        instructions ??= "";
        if (instructions.Length > 2000 || instructions.Any(char.IsControl))
            throw new ArgumentException("Breeze 声音指令最多 2000 字符，不能含控制字符。");
        if (Array.IndexOf(Formats, responseFormat) < 0)
            throw new ArgumentException("Breeze 输出格式只能是 pcm、wav 或 mp3。");
        if (timeout < TimeSpan.FromSeconds(3) || timeout > TimeSpan.FromSeconds(120))
            throw new ArgumentException("Breeze 超时必须是 3～120 秒。");
        endpoint = new Uri(baseUrl.TrimEnd('/') + "/v1/audio/speech");
        this.voiceId = id.Length == 0 ? null : id;
        this.instructions = instructions;
        format = responseFormat;
        this.timeout = timeout;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.log = log;
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        if (handler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseProxy = false; }
        if (handler is HttpClientHandler client) { client.AllowAutoRedirect = false; client.UseProxy = false; }
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>合成一条语音并转成 16kHz 单声道 WAV；失败时抛异常，由 SpeechEngine 决定是否回退。</summary>
    public async Task<byte[]> SpeechWavAsync(string text, string? emotion, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (clock() < skipUntil) throw new InvalidDataException("Breeze 刚失败过，暂时跳过。");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var pcm = await RequestAsync(text, emotion, ct).ConfigureAwait(false);
                    skipUntil = default;
                    return QqVoice.Wav(pcm);
                }
                catch (BreezeBusyException) when (attempt < 2)
                {
                    log?.Invoke("breeze_busy", new { attempt });
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), ct).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    skipUntil = clock() + Cooldown;
                    throw;
                }
            }
        }
        finally { gate.Release(); }
    }

    private async Task<byte[]> RequestAsync(string text, string? emotion, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = Body(text, emotion) };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict) throw new BreezeBusyException();
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaximumAudioBytes)
                throw new InvalidDataException("Breeze 没有合成成功（HTTP " + (int)response.StatusCode + "）。");
            var declared = response.Content.Headers.ContentType?.MediaType;
            var rate = SampleRate(response);
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaximumAudioBytes) throw new InvalidDataException("Breeze 返回的音频过大。");
                output.Write(buffer, 0, count);
            }
            deadline.Token.ThrowIfCancellationRequested();
            return Decode(output.ToArray(), declared, rate);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Breeze 在 " + (int)timeout.TotalSeconds + " 秒内没有返回音频。");
        }
    }

    private StringContent Body(string text, string? emotion)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = "breeze-tts-2",
            ["input"] = text,
            ["instructions"] = instructions.Length == 0 ? VoiceEmotion.Instruction(emotion) : instructions + " " + VoiceEmotion.Instruction(emotion),
            ["response_format"] = format
        };
        // 没保存过音色时不给 voice：让服务按指令设计音色，绝不拿一个不存在的 ID 去换 404。
        if (voiceId is not null) payload["voice"] = voiceId;
        var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
        return content;
    }

    private static int SampleRate(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Sample-Rate", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var rate) && rate is >= 8000 and <= 96000)
            return rate;
        return DefaultSampleRate;
    }

    /// <summary>先看真实字节再信 Content-Type：服务在没编 MP3 的构建上会把 mp3 请求回成 WAV。</summary>
    private static string Kind(byte[] bytes, string? declared)
    {
        if (Looks(bytes, "RIFF"u8) && bytes.Length >= 12 && Looks(bytes.AsSpan(8), "WAVE"u8)) return "wav";
        if (Looks(bytes, "ID3"u8)) return "mp3";
        if (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0) return "mp3";
        var media = (declared ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (media is "audio/wav" or "audio/x-wav" or "audio/wave") return "wav";
        if (media == "audio/mpeg") return "mp3";
        if (media is "audio/pcm" or "audio/l16" or "application/octet-stream") return "pcm";
        if (media.StartsWith("audio/", StringComparison.Ordinal)) return "pcm";
        throw new InvalidDataException("Breeze 返回的不是音频数据。");
    }

    private static bool Looks(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> prefix) =>
        bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);

    /// <summary>把 Breeze 的 pcm/wav/mp3 统一解成 16kHz 单声道 16 位 PCM；超过 60 秒的整条丢掉。</summary>
    private static byte[] Decode(byte[] bytes, string? declared, int headerRate)
    {
        var kind = Kind(bytes, declared);
        const int target = LocalSpeechSynthesis.SampleRate;
        const long maximum = (long)target * 2 * 60;
        WaveStream source = kind switch
        {
            "wav" => new WaveFileReader(new MemoryStream(bytes, writable: false)),
            "mp3" => new StreamMediaFoundationReader(new MemoryStream(bytes, writable: false), null, "audio/mpeg", "speech.mp3"),
            _ => new RawSourceWaveStream(new MemoryStream(bytes, writable: false), new WaveFormat(headerRate, 16, 1))
        };
        using (source)
        {
            var waveFormat = source.WaveFormat;
            if (waveFormat.Channels != 1) throw new InvalidDataException("Breeze 返回了多声道音频，QQ 语音只接受单声道。");
            ISampleProvider samples = source.ToSampleProvider();
            if (waveFormat.SampleRate != target) samples = new WdlResamplingSampleProvider(samples, target);
            var provider = new SampleToWaveProvider16(samples);
            using var output = new MemoryStream();
            var buffer = new byte[target * 2];
            int read;
            while ((read = provider.Read(buffer)) > 0)
            {
                if (output.Length + read > maximum) throw new InvalidDataException("Breeze 生成的语音超过 60 秒，QQ 语音放不下。");
                output.Write(buffer, 0, read);
            }
            var pcm = output.ToArray();
            if (pcm.Length < 2 || pcm.Length % 2 != 0) throw new InvalidDataException("Breeze 没有返回可用的音频。");
            return pcm;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        http.Dispose();
        gate.Dispose();
    }
}

/// <summary>
/// 语音引擎：按配置选用 Breeze 或原有本机 SAPI。Breeze 不可用、报错或生成失败时自动回退，
/// 语音失败绝不影响文字回复。语气优先取模型标注的情绪，没标时按这位联系人当前的好感/占有欲兜底。
/// 配置了留档目录时，每条合成成功的语音顺手存一份 WAV：QQ 自己不保存已发出的语音。
/// </summary>
public sealed class SpeechEngine : IDisposable
{
    private readonly VoiceSettings settings;
    private readonly CompanionStore? companions;
    private readonly BreezeTtsClient? breeze;
    private readonly Func<string, CancellationToken, Task<byte[]>> local;
    private readonly Action<string, object>? log;
    private readonly string? archiveDirectory;
    private int disposed;

    public SpeechEngine(VoiceSettings settings, CompanionStore? companions = null, Action<string, object>? log = null,
        Func<string, CancellationToken, Task<byte[]>>? localSynthesis = null, HttpMessageHandler? breezeHandler = null,
        string? voiceArchiveDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        this.settings = settings;
        this.companions = companions;
        this.log = log;
        archiveDirectory = string.IsNullOrWhiteSpace(voiceArchiveDirectory) ? null : voiceArchiveDirectory;
        local = localSynthesis ?? ((text, token) => QqVoice.SpeechWavAsync(text, settings, token));
        if (settings.TtsProvider != "breeze") return;
        breeze = new BreezeTtsClient(settings.BreezeBaseUrl, settings.BreezeVoiceId, settings.BreezeInstructions,
            settings.BreezeResponseFormat, TimeSpan.FromSeconds(settings.BreezeTimeoutSeconds), breezeHandler, log: log);
        if (settings.BreezeVoiceId.Length == 0)
            log?.Invoke("breeze_voice_design", new { note = "没有配置 breezeVoiceId，按声音描述设计音色；保存一份 voice 才能固定声线。" });
    }

    /// <summary>是否启用按情绪改语气（只有 Breeze 有指令通道，SAPI 没有）。</summary>
    public bool EmotionEnabled => breeze is not null && settings.BreezeEmotionEnabled;

    public async Task<byte[]> SynthesizeAsync(VoiceCue cue, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var spoken = QqVoice.Limit((cue.Text ?? "").Trim(), settings.MaximumCharacters);
        if (spoken.Length == 0) throw new ArgumentException("没有可朗读的正文。");
        var emotion = EmotionEnabled ? cue.Emotion ?? ToneFromState(cue.UserId) : null;
        if (breeze is not null)
        {
            try
            {
                var wav = await breeze.SpeechWavAsync(spoken, emotion, ct).ConfigureAwait(false);
                log?.Invoke("breeze_voice", new { characters = spoken.Length, emotion = emotion ?? "none", bytes = wav.Length });
                Archive(wav, spoken, "breeze", emotion);
                return wav;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                log?.Invoke("breeze_voice_failed", new { type = error.GetType().Name, code = ReplyFailure.From(error).Code, emotion = emotion ?? "none" });
                if (!settings.TtsFallbackEnabled) throw;
                log?.Invoke("voice_fallback", new { engine = "edge" });
            }
        }
        var voice = await local(spoken, ct).ConfigureAwait(false);
        log?.Invoke("edge_voice", new { characters = spoken.Length });
        Archive(voice, spoken, "edge", null);
        return voice;
    }

    /// <summary>语音留档：文件名带时间、引擎和情绪，日志里连朗读稿一起记，出问题能对上到底发了什么。</summary>
    private void Archive(byte[] wav, string spoken, string engine, string? emotion)
    {
        if (archiveDirectory is null) return;
        try
        {
            Directory.CreateDirectory(archiveDirectory);
            var file = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) +
                "-" + engine + "-" + (emotion ?? "none") + "-" + spoken.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".wav";
            File.WriteAllBytes(Path.Combine(archiveDirectory, file), wav);
            log?.Invoke("voice_saved", new { file, engine, emotion = emotion ?? "none", characters = spoken.Length, bytes = wav.Length, text = spoken });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { log?.Invoke("voice_save_failed", new { type = error.GetType().Name }); }
    }

    /// <summary>模型没标情绪时的兜底：只按这位联系人已有的好感/占有欲取，不猜关键词。</summary>
    private string? ToneFromState(long userId)
    {
        if (companions is null || userId <= 0) return null;
        try
        {
            var snapshot = companions.Get(userId);
            if (snapshot.Possession >= 60 && snapshot.Affection >= 200) return "angry";
            if (snapshot.Affection >= 600) return "comfort";
            return snapshot.Affection >= -199 ? VoiceEmotion.Normal : "angry";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or TimeoutException)
        { log?.Invoke("voice_tone_unavailable", new { userId, type = error.GetType().Name }); return null; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        breeze?.Dispose();
    }
}
