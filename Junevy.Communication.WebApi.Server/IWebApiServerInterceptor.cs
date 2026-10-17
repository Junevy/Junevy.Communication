namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// 服务端拦截器（P32-D10）：在认证之后、去重与处理器之前执行请求阶段，在写出响应之前执行响应阶段。
/// 请求阶段按注册顺序执行，响应阶段按逆序执行；任一方法抛出异常时该请求返回 500 并记录 Error。
/// 实现必须线程安全，因为同一个拦截器会被并发的请求共用。
/// </summary>
public interface IWebApiServerInterceptor
{
    /// <summary>请求阶段：可以检查请求，或调用 <see cref="WebApiServerRequestingContext.Reject"/> 直接给出响应。</summary>
    /// <param name="context">请求阶段的上下文。</param>
    /// <param name="cancellationToken">请求的取消令牌（超时或对端断开时触发）。</param>
    /// <returns>拦截完成的任务。</returns>
    Task OnRequestAsync(WebApiServerRequestingContext context, CancellationToken cancellationToken);

    /// <summary>响应阶段：可以在响应写出之前追加响应头。</summary>
    /// <param name="context">响应阶段的上下文。</param>
    /// <param name="cancellationToken">请求的取消令牌。</param>
    /// <returns>拦截完成的任务。</returns>
    Task OnRespondingAsync(WebApiServerRespondingContext context, CancellationToken cancellationToken);
}
