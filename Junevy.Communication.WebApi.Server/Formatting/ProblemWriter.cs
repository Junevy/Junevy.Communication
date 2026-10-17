using System.Buffers;
using System.Text.Json;

namespace Junevy.Communication.WebApi.Server.Formatting;

/// <summary>
/// RFC 9457 Problem Details 的写出（内部，P32-D6）。业务码放在扩展字段 <c>code</c> 中。
/// 不使用 ASP.NET Core 的 <c>ProblemDetails</c> 类型：它属于 <c>Microsoft.AspNetCore.Mvc</c>，只允许出现在 Hosting 命名空间（P32-D16、Q30）。
/// </summary>
internal static class ProblemWriter
{
    /// <summary>RFC 9457 规定的内容类型。</summary>
    public const string ContentType = "application/problem+json";

    /// <summary>写出 Problem Details。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="code">业务码（扩展字段 <c>code</c>）。</param>
    /// <param name="detail">消息（<c>detail</c>）。</param>
    /// <returns>UTF-8 JSON 字节。</returns>
    public static byte[] Write(int statusCode, string code, string detail)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "about:blank");
            writer.WriteString("title", ReasonPhrases.Of(statusCode));
            writer.WriteNumber("status", statusCode);
            writer.WriteString("detail", detail);
            writer.WriteString("code", code);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
