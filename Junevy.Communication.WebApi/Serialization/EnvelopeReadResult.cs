namespace Junevy.Communication.WebApi.Serialization;

/// <summary>
/// 从响应正文读出的业务包络（内部）：业务码、消息与 data 的原始 JSON；或者无法读出时的英文原因。
/// </summary>
internal sealed class EnvelopeReadResult
{
    private EnvelopeReadResult(bool isValid, string? error, string? code, string? message, string? dataJson)
    {
        IsValid = isValid;
        Error = error;
        Code = code;
        Message = message;
        DataJson = dataJson;
    }

    /// <summary>是否读出了业务码。</summary>
    public bool IsValid { get; }

    /// <summary>无法读出时的原因（英文）；读出时为 null。</summary>
    public string? Error { get; }

    /// <summary>业务码的 JSON 原文（字符串取其值，数字与布尔取其文本）。</summary>
    public string? Code { get; }

    /// <summary>消息；缺失或为 null 时为 null，不是字符串时为其 JSON 文本。</summary>
    public string? Message { get; }

    /// <summary>data 的原始 JSON 文本；缺失或为 null 时为 null。</summary>
    public string? DataJson { get; }

    /// <summary>创建成功读出的结果。</summary>
    /// <param name="code">业务码。</param>
    /// <param name="message">消息。</param>
    /// <param name="dataJson">data 的原始 JSON。</param>
    /// <returns>结果。</returns>
    public static EnvelopeReadResult Valid(string code, string? message, string? dataJson)
        => new EnvelopeReadResult(true, null, code, message, dataJson);

    /// <summary>创建无法读出的结果。</summary>
    /// <param name="error">原因（英文）。</param>
    /// <returns>结果。</returns>
    public static EnvelopeReadResult Invalid(string error)
        => new EnvelopeReadResult(false, error, null, null, null);
}
