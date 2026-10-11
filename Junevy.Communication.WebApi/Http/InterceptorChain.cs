using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 拦截器链（内部，P3-D3）：配置生成的认证拦截器排第一，后面是用户拦截器。发送前按注册顺序执行，收到响应后按逆序执行（洋葱模型，与 <c>DelegatingHandler</c> 一致）。
/// 任何拦截器调用了 <c>Fail</c> 或抛出异常，链立即停止；超时 / 取消产生的取消异常原样向上传播，由尝试循环按作用域归类。
/// </summary>
internal sealed class InterceptorChain
{
    private readonly ILogger logger;

    private InterceptorChain(IReadOnlyList<IWebApiInterceptor> interceptors, ILogger logger)
    {
        Interceptors = interceptors;
        this.logger = logger;
    }

    /// <summary>链上的拦截器，按发送前的执行顺序。</summary>
    public IReadOnlyList<IWebApiInterceptor> Interceptors { get; }

    /// <summary>
    /// 组装拦截器链。
    /// </summary>
    /// <param name="authentication">由配置创建的认证拦截器；没有时为 null。</param>
    /// <param name="userInterceptors">用户拦截器，按添加顺序。</param>
    /// <param name="logger">记录拦截器异常的记录器。</param>
    /// <returns>拦截器链。</returns>
    public static InterceptorChain Create(IWebApiInterceptor? authentication, IEnumerable<IWebApiInterceptor> userInterceptors, ILogger logger)
    {
        var interceptors = new List<IWebApiInterceptor>();
        if (authentication != null)
            interceptors.Add(authentication);
        interceptors.AddRange(userInterceptors);
        return new InterceptorChain(interceptors, logger);
    }

    /// <summary>
    /// 按注册顺序执行 <see cref="IWebApiInterceptor.OnSendingAsync"/>。
    /// </summary>
    /// <param name="context">本次尝试的请求上下文。</param>
    /// <param name="cancellationToken">本次尝试的令牌。</param>
    /// <returns>中止本次调用的失败（<c>Fail</c> 或异常）；全部正常为 null。</returns>
    public async Task<CallFailure?> RunSendingAsync(WebApiSendingContext context, CancellationToken cancellationToken)
    {
        foreach (IWebApiInterceptor interceptor in Interceptors)
        {
            try
            {
                await interceptor.OnSendingAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 边界：用户代码。异常转换为失败，不能让一个拦截器的 bug 打断管线。
                return Faulted(interceptor, ex);
            }

            if (context.Failure != null)
                return context.Failure;
        }

        return null;
    }

    /// <summary>
    /// 按注册的逆序执行 <see cref="IWebApiInterceptor.OnReceivedAsync"/>。
    /// </summary>
    /// <param name="context">本次尝试的响应上下文。</param>
    /// <param name="cancellationToken">本次尝试的令牌。</param>
    /// <returns>改判本次结果的失败（<c>Fail</c> 或异常）；全部正常为 null。</returns>
    public async Task<CallFailure?> RunReceivedAsync(WebApiReceivedContext context, CancellationToken cancellationToken)
    {
        for (int i = Interceptors.Count - 1; i >= 0; i--)
        {
            IWebApiInterceptor interceptor = Interceptors[i];
            try
            {
                await interceptor.OnReceivedAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Faulted(interceptor, ex);
            }

            if (context.Failure != null)
                return context.Failure;
        }

        return null;
    }

    private CallFailure Faulted(IWebApiInterceptor interceptor, Exception exception)
    {
        string type = interceptor.GetType().Name;
        logger.LogError(exception, "The interceptor {Interceptor} threw an exception; the call is aborted.", type);
        return CallFailure.Create($"The interceptor {type} failed with {exception.GetType().Name}: {exception.Message}", CommErrorKind.Unspecified, exception);
    }
}
