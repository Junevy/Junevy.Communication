using System.Xml.Linq;

namespace Junevy.Communication.WebApi.Soap;

/// <summary>
/// SOAP 客户端：把请求发给 MES 的 SOAP 服务并解释响应。运行期失败都体现在 <see cref="WebApiResult{T}"/> 里，不抛出异常。
/// 不使用 WCF：库无法为未知的 MES 预先生成强类型代理，WCF 的超时、重试与日志也无法与本库统一。
/// </summary>
public interface ISoapClient : IWebApiEndpoint
{
    /// <summary>构造客户端时传入的配置对象（客户端使用的是构造时的快照，之后修改它不生效）。</summary>
    SoapClientConfig Config { get; }

    /// <summary>
    /// 强类型调用：<paramref name="body"/> 用 <c>XmlSerializer</c> 序列化为 SOAP Body 的子元素，响应 Body 的第一个子元素反序列化为 <typeparamref name="TResponse"/>。
    /// </summary>
    /// <typeparam name="TRequest">请求类型（公开类型，有公开的无参构造函数）。</typeparam>
    /// <typeparam name="TResponse">响应类型。</typeparam>
    /// <param name="action">SOAPAction；可以是空串，不能为 null，也不能含引号或换行。</param>
    /// <param name="body">请求对象；不能为 null。</param>
    /// <param name="options">本次调用的可选项；可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>调用结果；SOAP Fault 是 <c>RemoteError</c>，<c>RemoteCode</c> 为 faultcode。</returns>
    Task<WebApiResult<TResponse>> InvokeAsync<TRequest, TResponse>(string action, TRequest body, SoapCallOptions? options = null,
                                                                  CancellationToken cancellationToken = default);

    /// <summary>
    /// 原始 XML 调用：WSDL 结构复杂、不想建模时使用。<paramref name="body"/> 原样写入 SOAP Body，结果是响应 Body 的第一个子元素。
    /// </summary>
    /// <param name="action">SOAPAction；可以是空串，不能为 null，也不能含引号或换行。</param>
    /// <param name="body">请求的 Body 子元素；不能为 null。</param>
    /// <param name="options">本次调用的可选项；可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>调用结果；响应 Body 为空时数据为 null。</returns>
    Task<WebApiResult<XElement>> InvokeRawAsync(string action, XElement body, SoapCallOptions? options = null, CancellationToken cancellationToken = default);
}
