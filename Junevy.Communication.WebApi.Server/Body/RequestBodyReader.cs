using System.Text;
using Junevy.Communication.WebApi.Server.Config;

namespace Junevy.Communication.WebApi.Server.Body;

/// <summary>
/// 请求正文的读取与解码（内部，P32-D7）：空正文返回 400；有正文但 Content-Type 不是 JSON 返回 415；
/// <c>byte[]</c> 给原始字节；<c>string</c> 按 charset 解码（缺省 UTF-8，不支持的字符集返回 415）；其余类型交给序列化器反序列化，失败返回 400。
/// 不接触 HttpContext：输入是字节与头，输出是 <see cref="BodyReadResult"/>。
/// </summary>
internal static class RequestBodyReader
{
    /// <summary>读取并解码请求正文。</summary>
    /// <param name="body">原始正文字节。</param>
    /// <param name="contentType">请求的 <c>Content-Type</c>；没有时为 null。</param>
    /// <param name="requestType">处理器声明的请求类型。</param>
    /// <param name="serializer">JSON 序列化器。</param>
    /// <returns>读取结果。</returns>
    public static BodyReadResult Read(byte[] body, string? contentType, Type requestType, IWebApiSerializer serializer)
    {
        if (body.Length == 0)
            return BodyReadResult.Fail(400, "The request body is empty.");
        if (!HttpSyntax.IsJsonMediaType(contentType))
            return BodyReadResult.Fail(415, "The request Content-Type must be a JSON media type.");
        if (requestType == typeof(byte[]))
            return BodyReadResult.Success(body);
        if (requestType == typeof(string))
            return DecodeText(body, contentType);

        return Deserialize(body, requestType, serializer);
    }

    private static BodyReadResult DecodeText(byte[] body, string? contentType)
    {
        Encoding encoding;
        try
        {
            encoding = CharsetOf(contentType);
        }
        catch (ArgumentException ex)
        {
            return BodyReadResult.Fail(415, "The request charset is not supported.", ex);
        }

        return BodyReadResult.Success(encoding.GetString(body));
    }

    // 只使用 BCL 内置的编码（UTF-8、UTF-16、ISO-8859-1 等）；不注册代码页提供程序，避免改变进程级状态。
    private static Encoding CharsetOf(string? contentType)
    {
        string? charset = ParameterOf(contentType, "charset");
        return charset == null ? Encoding.UTF8 : Encoding.GetEncoding(charset);
    }

    private static string? ParameterOf(string? contentType, string name)
    {
        if (contentType == null)
            return null;

        foreach (string parameter in contentType.Split(';').Skip(1))
        {
            int equals = parameter.IndexOf('=');
            if (equals >= 0 && parameter[..equals].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return parameter[(equals + 1)..].Trim().Trim('"');
        }

        return null;
    }

    private static BodyReadResult Deserialize(byte[] body, Type requestType, IWebApiSerializer serializer)
    {
        try
        {
            return BodyReadResult.Success(serializer.Deserialize(body, requestType));
        }
        catch (Exception ex)
        {
            // 序列化器是可替换的边界：任何异常都翻译成 400，详情只交给日志（P32-D7）。
            return BodyReadResult.Fail(400, "The request body could not be deserialized.", ex);
        }
    }
}
