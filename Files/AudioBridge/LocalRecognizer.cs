using SherpaOnnx;

namespace AudioBridge;

public sealed record RecognitionUpdate(string Text, bool IsFinal);

/// <summary>
/// CPU-only streaming Chinese ASR. One consumer owns this instance; its methods
/// must not run concurrently. Samples are mono float PCM in [-1, 1]. Sherpa
/// resamples internally, so the source can remain at 44.1/48 kHz.
/// </summary>
public sealed class LocalRecognizer : IDisposable
{
    public const string ModelName = "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23";
    private readonly OnlineRecognizer _recognizer;
    private OnlineStream _stream;
    private string _lastPartial = "";
    private int _inputSampleRate;
    private bool _disposed;

    public LocalRecognizer(string modelDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDir);
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("本地语音识别需要 Windows x64 进程。");

        string RequireFile(string name)
        {
            var path = Path.GetFullPath(Path.Combine(modelDir, name));
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new FileNotFoundException("本地模型缺失，请先运行 Files/Install-LocalAsr.ps1。", path);
            return path;
        }

        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = RequireFile("encoder-epoch-99-avg-1.int8.onnx");
        config.ModelConfig.Transducer.Decoder = RequireFile("decoder-epoch-99-avg-1.onnx");
        config.ModelConfig.Transducer.Joiner = RequireFile("joiner-epoch-99-avg-1.int8.onnx");
        config.ModelConfig.Tokens = RequireFile("tokens.txt");
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = 2;
        config.DecodingMethod = "greedy_search";
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = 2.4F;
        config.Rule2MinTrailingSilence = 0.8F;
        config.Rule3MinUtteranceLength = 20;
        _recognizer = new OnlineRecognizer(config);
        try { _stream = _recognizer.CreateStream(); }
        catch { _recognizer.Dispose(); throw; }
    }

    public IEnumerable<RecognitionUpdate> Accept(float[] monoSamples, int sampleRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(monoSamples);
        if (sampleRate is < 8000 or > 192000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "采样率必须为 8000～192000 Hz。");
        if (_inputSampleRate != 0 && _inputSampleRate != sampleRate)
            throw new ArgumentException("输入采样率发生变化，请先 Reset 后重建当前语句。", nameof(sampleRate));
        if (monoSamples.Length > sampleRate * 5)
            throw new ArgumentException("单次输入不能超过 5 秒，请按小帧持续送入。", nameof(monoSamples));
        foreach (var sample in monoSamples)
            if (!float.IsFinite(sample))
                throw new ArgumentException("输入包含非有限音频值。", nameof(monoSamples));
        if (monoSamples.Length == 0) return Array.Empty<RecognitionUpdate>();

        _inputSampleRate = sampleRate;
        _stream.AcceptWaveform(sampleRate, monoSamples);
        return Drain();
    }

    /// <summary>Finish pending audio and create a fresh stream for subsequent use.</summary>
    public IEnumerable<RecognitionUpdate> Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inputSampleRate == 0) return Array.Empty<RecognitionUpdate>();
        _stream.AcceptWaveform(_inputSampleRate, new float[_inputSampleRate / 2]);
        _stream.InputFinished();
        var result = Drain(forceFinal: true);
        Reset();
        return result;
    }

    /// <summary>Discard the entire current utterance, including resampler state.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = _stream;
        _stream = _recognizer.CreateStream();
        previous.Dispose();
        _lastPartial = "";
        _inputSampleRate = 0;
    }

    private IEnumerable<RecognitionUpdate> Drain(bool forceFinal = false)
    {
        while (_recognizer.IsReady(_stream)) _recognizer.Decode(_stream);
        var text = _recognizer.GetResult(_stream).Text.Trim();
        var isFinal = forceFinal || _recognizer.IsEndpoint(_stream);
        RecognitionUpdate[] updates = !string.IsNullOrWhiteSpace(text) && (isFinal || text != _lastPartial)
            ? [new(text, isFinal)] : [];
        _lastPartial = text;
        if (isFinal && !forceFinal)
        {
            _recognizer.Reset(_stream);
            _lastPartial = "";
        }
        return updates;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
        _recognizer.Dispose();
    }
}
