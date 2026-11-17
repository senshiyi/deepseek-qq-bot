using System.Buffers.Binary;
using System.Text.Json;
using NAudio.Wave;

namespace AudioBridge;

/// <summary>QQ 语音开关、引擎与音色；单独放在 voice.local.json，避免与群消息设置互相覆盖。</summary>
public sealed record VoiceSettings
{
    public bool Enabled { get; init; } = true;
    /// <summary>edge = 原有微软 SAPI；breeze = 本地 Breeze TTS 2 服务。</summary>
    public string TtsProvider { get; init; } = "edge";
    /// <summary>回退与 edge 引擎用的 SAPI 音色名。</summary>
    public string Voice { get; init; } = "Microsoft Huihui Desktop";
    public int SpeechRate { get; init; }
    public int MaximumCharacters { get; init; } = 120;
    public string BreezeBaseUrl { get; init; } = "http://127.0.0.1:8080";
    /// <summary>已保存的音色 ID（breeze-server 的 voices 目录里那份）；留空则按指令现场设计音色。</summary>
    public string BreezeVoiceId { get; init; } = "";
    /// <summary>声音描述：设计音色时是声线，克隆时是表达风格；每种情绪会在后面再追加一句语气。</summary>
    public string BreezeInstructions { get; init; } = "一位十八岁左右的活泼少女，声音清亮甜美，略带俏皮和傲娇感，语气灵动自然，有一点小得意。普通话标准，音色可爱但不尖锐，不要刻意卖萌。情绪变化丰富，说话像真实的年轻女孩";
    /// <summary>pcm（无容器，最省事）、wav 或 mp3。</summary>
    public string BreezeResponseFormat { get; init; } = "pcm";
    /// <summary>Breeze 大约按 1 倍速生成：120 字要三十多秒，20 秒的旧默认值会把整段长句误判成失败。</summary>
    public int BreezeTimeoutSeconds { get; init; } = 60;
    /// <summary>Breeze 失败时是否改用原有的微软 TTS，而不是整条语音不发。</summary>
    public bool TtsFallbackEnabled { get; init; } = true;
    /// <summary>按回复的情绪换语气；关掉就是固定声线的自然朗读。</summary>
    public bool BreezeEmotionEnabled { get; init; } = true;

    public static VoiceSettings Load(string path)
    {
        if (!File.Exists(path)) return new VoiceSettings();
        var value = JsonSerializer.Deserialize<VoiceSettings>(File.ReadAllText(path), BridgeConfig.JsonOptions)
            ?? throw new ArgumentException("语音配置为空。");
        value = value with
        {
            TtsProvider = (value.TtsProvider ?? "").Trim().ToLowerInvariant(),
            BreezeResponseFormat = (value.BreezeResponseFormat ?? "").Trim().ToLowerInvariant(),
            BreezeBaseUrl = (value.BreezeBaseUrl ?? "").Trim(),
            BreezeVoiceId = (value.BreezeVoiceId ?? "").Trim()
        };
        value.Validate();
        return value;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Voice) || Voice.Length > 96 || Voice.Any(char.IsControl) ||
            SpeechRate is < -3 or > 3 || MaximumCharacters is < 10 or > 300)
            throw new ArgumentException("语音配置无效：音色 1～96 字符，语速 -3～3，最长 10～300 字符。");
        if (TtsProvider?.ToLowerInvariant() is not ("edge" or "breeze"))
            throw new ArgumentException("语音引擎只能是 edge 或 breeze。");
        if (BreezeBaseUrl is null || BreezeBaseUrl.Length > 200 || !Uri.TryCreate(BreezeBaseUrl, UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new ArgumentException("Breeze 地址必须是 http/https 根地址，不能带账号、查询或锚点。");
        if (BreezeVoiceId is null || BreezeVoiceId.Length > 64 ||
            BreezeVoiceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("Breeze 音色 ID 只能用字母、数字、下划线和连字符，最多 64 字符。");
        if (BreezeInstructions is null || BreezeInstructions.Length > 2000 || BreezeInstructions.Any(char.IsControl))
            throw new ArgumentException("Breeze 声音指令最多 2000 字符，不能含控制字符。");
        if (BreezeResponseFormat?.ToLowerInvariant() is not ("pcm" or "wav" or "mp3"))
            throw new ArgumentException("Breeze 输出格式只能是 pcm、wav 或 mp3。");
        if (BreezeTimeoutSeconds is < 3 or > 120)
            throw new ArgumentException("Breeze 超时必须是 3～120 秒。");
    }
}

/// <summary>QQ 语音转文字：get_record 取 WAV，NAudio 解码，本地 sherpa 识别。一次只跑一条。</summary>
public sealed class QqVoiceReader(OneBotMessageClient client, string modelDirectory, Action<string, object>? log = null) : IDisposable
{
    public const int MaximumSeconds = 60;
    private readonly SemaphoreSlim gate = new(1, 1);
    private LocalRecognizer? recognizer;
    private bool disposed;

    /// <summary>返回识别出的文本；空白表示没听清，由调用方如实转述。</summary>
    public async Task<string> TranscribeAsync(string recordFile, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var path = await client.GetRecordAsync(recordFile, ct).ConfigureAwait(false);
            using var reader = new WaveFileReader(path);
            if (reader.TotalTime.TotalSeconds is <= 0 or > MaximumSeconds)
                throw new InvalidDataException("这条语音太短或超过 60 秒。");
            var format = reader.WaveFormat;
            var frame = Math.Max(format.BlockAlign, format.AverageBytesPerSecond / 50 / format.BlockAlign * format.BlockAlign);
            var buffer = new byte[frame];
            var active = recognizer ??= new LocalRecognizer(Path.GetFullPath(modelDirectory));
            var text = new List<string>();
            try
            {
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    foreach (var update in active.Accept(AudioFrames.ToMono(buffer.AsSpan(0, read), format), format.SampleRate))
                        if (update.IsFinal) text.Add(update.Text);
                for (var i = 0; i < 80; i++)
                    foreach (var update in active.Accept(new float[format.SampleRate / 50], format.SampleRate))
                        if (update.IsFinal) text.Add(update.Text);
                foreach (var update in active.Flush()) if (update.IsFinal) text.Add(update.Text);
            }
            catch
            {
                // 半路失败时丢弃这条语音的解码状态，下一条语音从干净状态开始。
                try { active.Reset(); } catch { }
                throw;
            }
            ct.ThrowIfCancellationRequested();
            var result = string.Join("", text).Trim();
            log?.Invoke("voice_transcribed", new { seconds = reader.TotalTime.TotalSeconds, characters = result.Length });
            return result;
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { gate.Wait(TimeSpan.FromSeconds(5)); } catch (ObjectDisposedException) { }
        finally
        {
            recognizer?.Dispose();
            recognizer = null;
            gate.Dispose();
        }
    }
}

/// <summary>把一段文字做成 QQ 语音消息：SAPI 合成 16kHz PCM，包成 WAV，交给 OneBot record 段。</summary>
public static class QqVoice
{
    public static async Task<byte[]> SpeechWavAsync(string text, VoiceSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var spoken = Limit(text?.Trim() ?? "", settings.MaximumCharacters);
        if (spoken.Length == 0) throw new ArgumentException("没有可朗读的正文。");
        var pcm = await LocalSpeechSynthesis.SynthesizePcmAsync(spoken, settings.Voice, settings.SpeechRate, ct).ConfigureAwait(false);
        return Wav(pcm);
    }

    /// <summary>16kHz/16位/单声道 WAV 容器；只包最后一段 PCM，不做任何重采样。</summary>
    public static byte[] Wav(byte[] pcm, int sampleRate = LocalSpeechSynthesis.SampleRate)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (pcm.Length == 0 || pcm.Length % 2 != 0 || pcm.Length > OneBotMessageClient.MaximumVoicePcmBytes)
            throw new InvalidDataException("语音 PCM 为空、不是16位样本或超过60秒上限。");
        var wav = new byte[44 + pcm.Length];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + pcm.Length));
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(sampleRate * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)pcm.Length);
        pcm.CopyTo(span[44..]);
        return wav;
    }

    /// <summary>按字数上限截断朗读稿；Breeze 与本机 SAPI 用同一条规则。</summary>
    public static string Limit(string text, int maximum) => text.Length <= maximum ? text :
        text[..(char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum)].TrimEnd();
}
