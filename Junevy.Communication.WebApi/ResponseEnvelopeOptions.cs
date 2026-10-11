namespace Junevy.Communication.WebApi;

/// <summary>
/// 业务包络的配置：MES 常把业务结果包在 JSON 里（例如 <c>{"code":0,"msg":"ok","data":{...}}</c>），HTTP 状态码为 200 也可能是业务失败。
/// 启用后，响应先按路径定位 code / msg / data，code 不在 <see cref="SuccessCodes"/> 中即判定为失败。
/// 包络字段的定位始终用 <c>JsonDocument</c>，与替换的序列化器无关。
/// </summary>
public sealed class ResponseEnvelopeOptions
{
    /// <summary>是否启用业务包络，默认 false。</summary>
    public bool Enabled { get; set; }

    /// <summary>业务码的点分路径，例如 <c>result.code</c>，默认 <c>code</c>。</summary>
    public string CodePath { get; set; } = "code";

    /// <summary>消息的点分路径，默认 <c>msg</c>。</summary>
    public string MessagePath { get; set; } = "msg";

    /// <summary>数据的点分路径，默认 <c>data</c>。</summary>
    public string DataPath { get; set; } = "data";

    /// <summary>表示成功的业务码，按 JSON 原文比较：数字 0 与字符串 "0" 都是 <c>"0"</c>。默认 <c>{ "0" }</c>。</summary>
    public string[] SuccessCodes { get; set; } = { "0" };
}
