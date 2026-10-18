using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// HTTP 服务端（P32-D5 至 D13）：宿主注册路由与处理器，MES 用 HTTP 与 JSON 调用。
/// 路由在 <see cref="StartAsync"/> 之前注册；请求的处理结果经 <see cref="RequestCompleted"/> 报告，每个请求恰好一次。
/// </summary>
public interface IWebApiServer : IDisposable, IAsyncDisposable
{
    /// <summary>服务器名称（用于日志与 Basic 认证的 realm）。</summary>
    string Name { get; }

    /// <summary>当前的生命周期状态。</summary>
    WebApiServerState State { get; }

    /// <summary>
    /// 监听地址：启动前为配置值；启动后为实际绑定的地址（端口 0 已解析）；停止后回到配置值。
    /// </summary>
    IReadOnlyList<string> Urls { get; }

    /// <summary>状态变化时触发，顺序与实际变化一致。订阅者的异常只记录，不影响服务器。</summary>
    event EventHandler<WebApiServerStateChangedEventArgs>? StateChanged;

    /// <summary>请求进入服务器（路由之前）时触发。</summary>
    event EventHandler<WebApiServerRequestEventArgs>? RequestReceived;

    /// <summary>请求结束时触发，每个请求恰好一次（包括被拒绝、超时、对端断开与去重重放）。</summary>
    event EventHandler<WebApiServerCompletedEventArgs>? RequestCompleted;

    /// <summary>
    /// 启动服务器并绑定地址。已启动、没有路由、地址被占用、无权绑定、证书加载失败等情况都以 <see cref="CommResult"/> 返回，不抛异常；
    /// 任何失败都不会留下半启动的应用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启动结果。</returns>
    /// <exception cref="ObjectDisposedException">服务器已释放。</exception>
    Task<CommResult> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止接收新请求，等待在途请求结束，最长 <c>StopTimeout</c>；之后中止剩余连接并取消处理器的令牌。可重复调用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌（同时缩短等待）。</param>
    /// <returns>停止完成的任务。</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>注册 GET 路由（无正文）。</summary>
    /// <typeparam name="TResponse">数据类型。</typeparam>
    /// <param name="template">ASP.NET Core 路由模板，例如 <c>/orders/{id:int}</c>。</param>
    /// <param name="handler">处理器。</param>
    /// <returns>本服务器，便于链式调用。</returns>
    /// <exception cref="ArgumentException">模板为空，或同一方法下模板重复。</exception>
    /// <exception cref="InvalidOperationException">服务器已经启动过。</exception>
    IWebApiServer MapGet<TResponse>(string template, Func<WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler);

    /// <summary>注册 DELETE 路由（无正文）。</summary>
    /// <typeparam name="TResponse">数据类型。</typeparam>
    /// <param name="template">路由模板。</param>
    /// <param name="handler">处理器。</param>
    /// <returns>本服务器，便于链式调用。</returns>
    /// <exception cref="ArgumentException">模板为空，或同一方法下模板重复。</exception>
    /// <exception cref="InvalidOperationException">服务器已经启动过。</exception>
    IWebApiServer MapDelete<TResponse>(string template, Func<WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler);

    /// <summary>注册 POST 路由（JSON 正文）。</summary>
    /// <typeparam name="TRequest">请求正文的类型。</typeparam>
    /// <typeparam name="TResponse">数据类型。</typeparam>
    /// <param name="template">路由模板。</param>
    /// <param name="handler">处理器。</param>
    /// <returns>本服务器，便于链式调用。</returns>
    /// <exception cref="ArgumentException">模板为空，或同一方法下模板重复。</exception>
    /// <exception cref="InvalidOperationException">服务器已经启动过。</exception>
    IWebApiServer MapPost<TRequest, TResponse>(string template, Func<TRequest, WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler);

    /// <summary>注册 PUT 路由（JSON 正文）。</summary>
    /// <typeparam name="TRequest">请求正文的类型。</typeparam>
    /// <typeparam name="TResponse">数据类型。</typeparam>
    /// <param name="template">路由模板。</param>
    /// <param name="handler">处理器。</param>
    /// <returns>本服务器，便于链式调用。</returns>
    /// <exception cref="ArgumentException">模板为空，或同一方法下模板重复。</exception>
    /// <exception cref="InvalidOperationException">服务器已经启动过。</exception>
    IWebApiServer MapPut<TRequest, TResponse>(string template, Func<TRequest, WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler);
}
