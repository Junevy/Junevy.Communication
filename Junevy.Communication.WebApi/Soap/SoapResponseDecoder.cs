using System.Xml.Linq;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Soap;

/// <summary>
/// 把 SOAP 响应解释为 <see cref="WebApiResult{T}"/>（内部，计划 Task 9 第 2 条）。顺序：解析信封 → Body 的第一个子元素是 Fault，
/// 无论状态码都是 <c>RemoteError</c>（<c>RemoteCode</c> 为 Fault 的代码，因此永远不会被重试）→ 状态码非 2xx 按状态码归类 →
/// 取 Body 的第一个子元素交给 <c>extract</c>（失败为 <c>ProtocolViolation</c>）。正文不是 SOAP 信封：非 2xx 按状态码归类，2xx 是 <c>ProtocolViolation</c>。
/// </summary>
internal sealed class SoapResponseDecoder
{
    /// <summary>
    /// 解码响应。不抛出异常：无法解释的响应都是失败结果。
    /// </summary>
    /// <typeparam name="T">结果的数据类型。</typeparam>
    /// <param name="response">收到的响应（正文已经过拦截器处理）。</param>
    /// <param name="extract">把 Body 的第一个子元素转换为结果数据；失败时抛出异常。</param>
    /// <returns>解释后的结果。</returns>
    public WebApiResult<T> Decode<T>(HttpExchangeResponse response, Func<XElement, T> extract)
    {
        CommErrorKind kind = HttpStatusClassifier.Classify(response.StatusCode);
        if (response.Body.Length == 0)
            return kind == CommErrorKind.None
                ? WebApiResult<T>.Success(default, response.StatusCode)
                : WebApiResult<T>.Fail(HttpStatusClassifier.Describe(response), kind, response.StatusCode);

        SoapMessage message = SoapMessage.Parse(response.Body);
        if (message.IsFault)
            return WebApiResult<T>.Fail(message.FaultMessage!, CommErrorKind.RemoteError, response.StatusCode, message.FaultCode);
        if (kind != CommErrorKind.None)
            return WebApiResult<T>.Fail(HttpStatusClassifier.Describe(response), kind, response.StatusCode);
        if (!message.IsEnvelope)
            return WebApiResult<T>.Fail("The response is not a SOAP envelope.", CommErrorKind.ProtocolViolation, response.StatusCode);

        return message.Content == null ? WebApiResult<T>.Success(default, response.StatusCode) : Extract(message.Content, extract, response.StatusCode);
    }

    // 边界：extract 里是 XmlSerializer 或调用方提供的转换，可以抛出任何异常；解不出来就是对端违反了约定。
    private static WebApiResult<T> Extract<T>(XElement content, Func<XElement, T> extract, int statusCode)
    {
        try
        {
            return WebApiResult<T>.Success(extract(content), statusCode);
        }
        catch (Exception ex)
        {
            return WebApiResult<T>.Fail($"The SOAP response could not be converted to {typeof(T).Name}: {ex.Message}", CommErrorKind.ProtocolViolation, statusCode, null, ex);
        }
    }
}
