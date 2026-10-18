namespace Junevy.Communication.WebApi;

/// <summary>
/// 请求拦截器（P3-D3）：在每次发送尝试前与收到响应后被调用，可以修改请求、中止调用、请求重放一次。
/// 事件只能旁观，需要签名、换 token、改请求头时用拦截器。执行顺序：发送前按注册顺序（配置生成的认证拦截器排第一），收到响应后按注册的逆序。
/// 实现必须线程安全（并发调用共享同一个实例），并且遵守取消令牌：令牌在尝试超时或调用取消时触发。
/// </summary>
public interface IWebApiInterceptor
{
    /// <summary>
    /// 每次发送尝试之前调用（重试与重放都会再次调用）。可以修改 <see cref="WebApiSendingContext"/> 的 URI、头与正文，或调用 <see cref="WebApiSendingContext.Fail"/> 中止调用。
    /// 抛出异常会以 <c>Unspecified</c> 结束调用（记 Error 日志，不重试）。
    /// </summary>
    /// <param name="context">本次尝试的请求上下文。</param>
    /// <param name="cancellationToken">本次尝试的令牌（超时或调用取消时触发）。</param>
    /// <returns>表示完成的任务。</returns>
    Task OnSendingAsync(WebApiSendingContext context, CancellationToken cancellationToken);

    /// <summary>
    /// 收到 HTTP 响应后调用（网络层失败时不调用）。可以替换正文、调用 <see cref="WebApiReceivedContext.Fail"/> 改判为失败，
    /// 或调用 <see cref="WebApiReceivedContext.RequestReplay"/> 请求立即再发一次。
    /// </summary>
    /// <param name="context">本次尝试的响应上下文。</param>
    /// <param name="cancellationToken">本次尝试的令牌（超时或调用取消时触发）。</param>
    /// <returns>表示完成的任务。</returns>
    Task OnReceivedAsync(WebApiReceivedContext context, CancellationToken cancellationToken);
}
