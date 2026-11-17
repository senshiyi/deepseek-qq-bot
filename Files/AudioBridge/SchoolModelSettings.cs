using System.Text.Json;

namespace AudioBridge;

public sealed record SchoolModelSettings
{
    public string BaseUrl { get; init; } = "https://myai.bupt.edu.cn/llm-gw/v1/";
    public string Model { get; init; } = "";
    /// <summary>可选的视觉模型ID；为空时学校入口保持纯文字，遇到图片只能请对方描述。</summary>
    public string? VisionModel { get; init; }
    public decimal? InputPointsPerMillion { get; init; }
    public decimal? OutputPointsPerMillion { get; init; }
    public decimal? MaxPoints { get; init; }
    public int? MaxCalls { get; init; } = 10;
    public bool Stream { get; init; }
    /// <summary>可选的 DNS 兜底：本机解析器把网关的内网 A 记录过滤掉时，直连这个 IP（TLS 仍按域名校验）。</summary>
    public string? ResolveIp { get; init; }

    public static SchoolModelSettings Load(string path = "school-api.local.json")
    {
        if (!File.Exists(path)) throw new ArgumentException("缺少学校模型本地配置；请填写已核实的模型和输入/输出点价，累计上限可显式设为null。");
        SchoolModelSettings settings;
        try { settings = JsonSerializer.Deserialize<SchoolModelSettings>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException(); }
        catch (JsonException) { throw new ArgumentException("学校模型本地配置不是有效的JSON。"); }
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            !url.Host.Equals("myai.bupt.edu.cn", StringComparison.OrdinalIgnoreCase) || !url.IsDefaultPort ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            url.AbsolutePath.TrimEnd('/') != "/llm-gw/v1" || BaseUrl.Contains('\\'))
            throw new ArgumentException("学校模型仅允许已核实的HTTPS学校网关地址。");
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 200 || Model.Any(char.IsWhiteSpace) || Model.Any(char.IsControl))
            throw new ArgumentException("请填写学校模型目录中的准确模型ID。");
        if (!string.IsNullOrWhiteSpace(VisionModel) && (VisionModel.Length > 200 ||
            VisionModel.Any(char.IsWhiteSpace) || VisionModel.Any(char.IsControl)))
            throw new ArgumentException("视觉模型ID须为学校模型目录中的准确ID，留空表示不读图。");
        if (InputPointsPerMillion is null or < 0 || OutputPointsPerMillion is null or < 0 || MaxPoints is < 0 || MaxCalls is < 1 or > 1000)
            throw new ArgumentException("学校输入/输出点价必须明确填写且不为负；累计点数上限须不为负，调用次数上限范围1～1000；null表示不设对应累计上限。");
        if (MaxPoints == 0 && (InputPointsPerMillion != 0 || OutputPointsPerMillion != 0))
            throw new ArgumentException("点数上限为0时仅允许用户已明确填写的双零价格。");
        if (!string.IsNullOrWhiteSpace(ResolveIp) && !System.Net.IPAddress.TryParse(ResolveIp.Trim(), out _))
            throw new ArgumentException("resolveIp 只能填一个有效的 IP 地址，留空表示按系统 DNS 解析。");
    }

    internal Uri ChatEndpoint => new(BaseUrl.TrimEnd('/') + "/chat/completions");
}
