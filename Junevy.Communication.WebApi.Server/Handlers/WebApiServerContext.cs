using System.Net;

namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// 一次请求的上下文：请求数据加上认证身份，以及拦截器与处理器之间传递数据的 <see cref="Items"/>。
/// 处理器、认证器与拦截器共用同一个实例。不暴露 ASP.NET Core 的 <c>HttpContext</c>，以后更换 HTTP 实现不影响公开 API。
/// </summary>
public sealed class WebApiServerContext
{
    /// <summary>创建请求上下文（由服务器在读取正文之后创建）。</summary>
    internal WebApiServerContext(
        string serverName,
        long requestId,
        string method,
        string path,
        string? routeTemplate,
        IReadOnlyDictionary<string, string> routeValues,
        IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> headers,
        string? contentType,
        byte[] body,
        IPEndPoint? remoteEndPoint)
    {
        ServerName = serverName;
        RequestId = requestId;
        Method = method;
        Path = path;
        RouteTemplate = routeTemplate;
        RouteValues = routeValues;
        Query = query;
        Headers = headers;
        ContentType = contentType;
        Body = body;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>服务器名称。</summary>
    public string ServerName { get; }

    /// <summary>本服务器内递增的请求编号。</summary>
    public long RequestId { get; }

    /// <summary>HTTP 方法，大写，例如 <c>POST</c>。</summary>
    public string Method { get; }

    /// <summary>请求路径（不含查询串），例如 <c>/orders/42</c>。</summary>
    public string Path { get; }

    /// <summary>命中的路由模板，例如 <c>/orders/{id}</c>；没有命中路由时为 null。</summary>
    public string? RouteTemplate { get; }

    /// <summary>路由参数，键为模板中的参数名，值为原始文本。</summary>
    public IReadOnlyDictionary<string, string> RouteValues { get; }

    /// <summary>查询参数；同名参数取第一个。</summary>
    public IReadOnlyDictionary<string, string> Query { get; }

    /// <summary>请求头，名称不区分大小写；同名多值以 <c>", "</c> 合并。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>请求的 <c>Content-Type</c>；没有时为 null。</summary>
    public string? ContentType { get; }

    /// <summary>原始请求正文（未解码）；没有正文时为空数组。</summary>
    public byte[] Body { get; }

    /// <summary>对端地址；取不到时为 null。</summary>
    public IPEndPoint? RemoteEndPoint { get; }

    /// <summary>认证通过时的身份；未认证或认证器没有给出身份时为 null。</summary>
    public string? UserName { get; internal set; }

    /// <summary>拦截器与处理器之间传递数据的字典（键区分大小写）。</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}
