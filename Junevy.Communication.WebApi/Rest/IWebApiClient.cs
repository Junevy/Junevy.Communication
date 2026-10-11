namespace Junevy.Communication.WebApi.Rest;

/// <summary>
/// REST 客户端：把请求发给 MES 并解释响应。运行期失败都体现在 <see cref="WebApiResult{T}"/> 里，不抛出异常。
/// </summary>
public interface IWebApiClient : IWebApiEndpoint
{
    /// <summary>构造客户端时传入的配置对象（客户端使用的是构造时的快照，之后修改它不生效）。</summary>
    WebApiClientConfig Config { get; }

    /// <summary>
    /// 发送一个请求。<paramref name="request"/> 为 null 或参数非法时，结果是 <c>InvalidRequest</c>；释放之后的调用结果是 <c>NotConnected</c>。
    /// </summary>
    /// <typeparam name="TResponse">响应数据的类型：<c>byte[]</c> 与 <c>string</c> 返回原始正文，其他类型经序列化器反序列化。</typeparam>
    /// <param name="request">请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>调用结果。</returns>
    Task<WebApiResult<TResponse>> SendAsync<TResponse>(WebApiRequest request, CancellationToken cancellationToken = default);
}
