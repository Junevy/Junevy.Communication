using System.Net.Http;

namespace Junevy.Communication.WebApi.Rest;

/// <summary>
/// 一次 REST 调用的请求。参数非法（<see cref="Path"/> 是绝对 URL、字节 / 字符串正文没有 <see cref="ContentType"/>、<see cref="Timeout"/> 不是正数等）
/// 不会抛出异常，调用以 <c>InvalidRequest</c> 失败，并触发 <c>RequestCompleted</c>（<c>Attempts = 0</c>）。
/// </summary>
public sealed class WebApiRequest
{
    /// <summary>HTTP 方法，默认 GET。</summary>
    public HttpMethod Method { get; set; } = HttpMethod.Get;

    /// <summary>
    /// 相对于基地址的路径，可以带查询串。必须是相对路径：绝对 URL（包括 <c>//host/x</c> 和 <c>scheme:x</c> 形式）会被拒绝，
    /// 否则认证头会被带到别的主机（P3-D17）。
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>查询参数；名称与值都会做 <c>Uri.EscapeDataString</c> 编码，值为 null 的项不发送。</summary>
    public IDictionary<string, string?>? Query { get; set; }

    /// <summary>
    /// 请求正文：<c>byte[]</c> 原样发送；<c>string</c> 按 <see cref="ContentType"/> 的 charset 编码（默认 UTF-8）；其他对象经序列化器序列化。为 null 表示没有正文。
    /// </summary>
    public object? Body { get; set; }

    /// <summary>正文的内容类型。<c>byte[]</c> 与 <c>string</c> 正文必须指定；对象正文默认使用序列化器的内容类型，指定了则覆盖它。</summary>
    public string? ContentType { get; set; }

    /// <summary>本次请求的头，覆盖配置里同名的 <see cref="WebApiEndpointConfig.DefaultHeaders"/>。</summary>
    public IDictionary<string, string>? Headers { get; set; }

    /// <summary>覆盖配置里的每次尝试超时（毫秒）；必须为正，null 表示使用配置值。</summary>
    public int? Timeout { get; set; }

    /// <summary>
    /// 幂等键：设置后作为请求头（头名见 <see cref="WebApiEndpointConfig.IdempotencyHeaderName"/>）发给对端，并允许重试 POST / PUT / PATCH / DELETE
    /// （对端据此去重）；以后的离线缓存也靠它去重。
    /// </summary>
    public string? IdempotencyKey { get; set; }
}
