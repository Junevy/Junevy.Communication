using System.Net.Http;

namespace Junevy.Communication.WebApi.Rest;

/// <summary>
/// <see cref="IWebApiClient"/> 的便捷方法：每个方法只是构造一个 <see cref="WebApiRequest"/> 并调用 <see cref="IWebApiClient.SendAsync{TResponse}"/>。
/// 需要自定义头、超时或内容类型时直接构造 <see cref="WebApiRequest"/>。
/// </summary>
public static class WebApiClientExtensions
{
    /// <summary>发送 GET 请求。</summary>
    /// <typeparam name="TResponse">响应数据的类型。</typeparam>
    /// <param name="client">客户端。</param>
    /// <param name="path">相对路径。</param>
    /// <param name="query">查询参数；可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>调用结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> 为 null。</exception>
    public static Task<WebApiResult<TResponse>> GetAsync<TResponse>(this IWebApiClient client, string path, IDictionary<string, string?>? query = null,
                                                                    CancellationToken cancellationToken = default)
        => Send<TResponse>(client, new WebApiRequest { Method = HttpMethod.Get, Path = path, Query = query }, cancellationToken);

    /// <summary>发送 POST 请求。</summary>
    /// <typeparam name="TResponse">响应数据的类型。</typeparam>
    /// <param name="client">客户端。</param>
    /// <param name="path">相对路径。</param>
    /// <param name="body">请求正文，见 <see cref="WebApiRequest.Body"/>。</param>
    /// <param name="idempotencyKey">幂等键；设置后允许重试。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>调用结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> 为 null。</exception>
    public static Task<WebApiResult<TResponse>> PostAsync<TResponse>(this IWebApiClient client, string path, object? body, string? idempotencyKey = null,
                                                                     CancellationToken cancellationToken = default)
        => Send<TResponse>(client, new WebApiRequest { Method = HttpMethod.Post, Path = path, Body = body, IdempotencyKey = idempotencyKey }, cancellationToken);

    /// <summary>发送 PUT 请求。</summary>
    /// <typeparam name="TResponse">响应数据的类型。</typeparam>
    /// <param name="client">客户端。</param>
    /// <param name="path">相对路径。</param>
    /// <param name="body">请求正文，见 <see cref="WebApiRequest.Body"/>。</param>
    /// <param name="idempotencyKey">幂等键；设置后允许重试。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>调用结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> 为 null。</exception>
    public static Task<WebApiResult<TResponse>> PutAsync<TResponse>(this IWebApiClient client, string path, object? body, string? idempotencyKey = null,
                                                                    CancellationToken cancellationToken = default)
        => Send<TResponse>(client, new WebApiRequest { Method = HttpMethod.Put, Path = path, Body = body, IdempotencyKey = idempotencyKey }, cancellationToken);

    /// <summary>发送 DELETE 请求。</summary>
    /// <typeparam name="TResponse">响应数据的类型。</typeparam>
    /// <param name="client">客户端。</param>
    /// <param name="path">相对路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>调用结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> 为 null。</exception>
    public static Task<WebApiResult<TResponse>> DeleteAsync<TResponse>(this IWebApiClient client, string path, CancellationToken cancellationToken = default)
        => Send<TResponse>(client, new WebApiRequest { Method = HttpMethod.Delete, Path = path }, cancellationToken);

    private static Task<WebApiResult<TResponse>> Send<TResponse>(IWebApiClient client, WebApiRequest request, CancellationToken cancellationToken)
    {
        if (client == null)
            throw new ArgumentNullException(nameof(client));

        return client.SendAsync<TResponse>(request, cancellationToken);
    }
}
