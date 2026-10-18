using System.Net;

namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 与 HTTP 实现无关的请求快照（内部）：由 Hosting 层从 <c>HttpContext</c> 构造，管线只看到这些数据与一个读取正文的委托。
/// </summary>
internal sealed class RawRequest
{
    private readonly Func<CancellationToken, Task<byte[]>> readBody;

    /// <summary>创建请求快照。</summary>
    /// <param name="method">HTTP 方法，大写。</param>
    /// <param name="path">请求路径。</param>
    /// <param name="query">查询参数（同名取第一个）。</param>
    /// <param name="headers">请求头（名称不区分大小写）。</param>
    /// <param name="contentType">内容类型；没有时为 null。</param>
    /// <param name="remoteEndPoint">对端地址；取不到时为 null。</param>
    /// <param name="routeTemplate">命中的路由模板；没有命中时为 null。</param>
    /// <param name="routeValues">路由参数。</param>
    /// <param name="readBody">读取完整正文的委托；超过上限时抛出 <see cref="RequestBodyTooLargeException"/>。</param>
    public RawRequest(
        string method,
        string path,
        IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> headers,
        string? contentType,
        IPEndPoint? remoteEndPoint,
        string? routeTemplate,
        IReadOnlyDictionary<string, string> routeValues,
        Func<CancellationToken, Task<byte[]>> readBody)
    {
        Method = method;
        Path = path;
        Query = query;
        Headers = headers;
        ContentType = contentType;
        RemoteEndPoint = remoteEndPoint;
        RouteTemplate = routeTemplate;
        RouteValues = routeValues;
        this.readBody = readBody;
    }

    /// <summary>HTTP 方法，大写。</summary>
    public string Method { get; }

    /// <summary>请求路径（不含查询串）。</summary>
    public string Path { get; }

    /// <summary>查询参数。</summary>
    public IReadOnlyDictionary<string, string> Query { get; }

    /// <summary>请求头，名称不区分大小写。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>内容类型；没有时为 null。</summary>
    public string? ContentType { get; }

    /// <summary>对端地址。</summary>
    public IPEndPoint? RemoteEndPoint { get; }

    /// <summary>命中的路由模板。</summary>
    public string? RouteTemplate { get; }

    /// <summary>路由参数。</summary>
    public IReadOnlyDictionary<string, string> RouteValues { get; }

    /// <summary>读取完整的请求正文。</summary>
    /// <param name="cancellationToken">取消令牌（超时或对端断开时触发）。</param>
    /// <returns>正文字节；没有正文时为空数组。</returns>
    public Task<byte[]> ReadBodyAsync(CancellationToken cancellationToken) => readBody(cancellationToken);
}
