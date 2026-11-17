namespace AudioBridge;

/// <summary>Stable diagnostics only. Exception messages and model output never become panel or log text.</summary>
internal sealed record ReplyFailure(string Code, string Message)
{
    private const string CodeKey = "AudioBridge.ReplyFailureCode";

    public static T Tag<T>(T error, string code) where T : Exception
    {
        if (Known(code) is null) throw new ArgumentOutOfRangeException(nameof(code));
        error.Data[CodeKey] = code;
        return error;
    }

    public static InvalidDataException InvalidDecision(string code)
    {
        var failure = Known(code) ?? throw new ArgumentOutOfRangeException(nameof(code));
        return Tag(new InvalidDataException(failure.Message), code);
    }

    /// <summary>调用方已打过固定码的异常：包装层不要再改写成笼统类型。</summary>
    internal static bool HasCode(Exception error) => error.Data[CodeKey] is string code && Known(code) is not null;

    public static ReplyFailure From(Exception error)
    {
        if (error.Data[CodeKey] is string code && Known(code) is { } known) return known;
        return error switch
        {
            OneBotMessageException { DeliveryMayHaveOccurred: true } => Known("delivery_unknown")!,
            OneBotMessageException => new("message_transport_failed", "消息接口未确认发送成功；本次不自动重发。"),
            InvalidDataException => new("invalid_data", "本次数据未通过校验，具体来源未记录。"),
            HttpRequestException => new("network_error", "网络请求失败，本次处理未完成。"),
            TimeoutException => new("timeout", "本次处理超时。"),
            OperationCanceledException => new("cancelled", "本次处理已取消。"),
            UnauthorizedAccessException => new("access_denied", "访问所需本地资源被拒绝。"),
            IOException => new("io_error", "模型请求、数据读取或本地存储失败，具体来源未记录。"),
            ArgumentException => new("invalid_input", "本次输入或配置不符合要求。"),
            InvalidOperationException => new("invalid_state", "当前状态不允许完成本次处理。"),
            _ => new("unexpected", "本次处理发生未预期错误，请检查本机运行状态。")
        };
    }

    private static ReplyFailure? Known(string code) => code switch
    {
        "invalid_json" => new(code, "模型回复不是可解析的 JSON，或超过允许的嵌套深度；本次已丢弃。"),
        "invalid_schema" => new(code, "模型回复的字段或类型不符合约定；本次已丢弃。"),
        "invalid_text" => new(code, "模型回复正文超长或含不允许的控制字符；本次已丢弃。"),
        "invalid_meme" => new(code, "模型选择的表情编号或类型无效；本次已丢弃。"),
        "meme_data_invalid" => new(code, "本地表情清单或素材未通过校验；本次处理未完成。"),
        "scheduled_failed" => new(code, "本次定时消息未完成，内层未提供详细错误分类；不会自动重发。"),
        "delivery_unknown" => new(code, "无法确认本次消息是否已送达，请先核对 QQ；不会自动重发。"),
        "background_loop_failed" => new(code, "后台循环异常退出，请检查本机诊断记录。"),
        "school_unavailable" => new(code, "学校模型接口暂时连不上或响应不完整；本次未处理，不会自动重试。"),
        "deepseek_auth" => new(code, "DeepSeek 拒绝了这个密钥（未授权或无权限）；请检查本地 .env。"),
        "deepseek_balance" => new(code, "DeepSeek 账户余额不足；本次未处理，不会自动重试。"),
        "deepseek_rate_limit" => new(code, "DeepSeek 请求太频繁；本次未处理，不会自动重试。"),
        "deepseek_rejected" => new(code, "DeepSeek 拒绝了这次请求（参数或内容）；本次未处理。"),
        "deepseek_unavailable" => new(code, "DeepSeek 接口连不上或响应不完整；本次未处理，不会自动重试。"),
        "deepseek_budget" => new(code, "DeepSeek 本轮的调用次数或累计金额预算已经用完；本次未调用，不重置累计。"),
        "school_budget" => new(code, "学校模型累计点数或调用次数已达配置上限；本次未调用，不重置累计。"),
        _ => null
    };
}
