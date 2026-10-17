namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 一次请求的拦截器执行（内部，P32-D10）。请求阶段按注册顺序执行，遇到 <c>Reject</c> 或异常即停止；
/// 响应阶段只通知请求阶段已经完成（或拒绝）的拦截器，并按逆序执行，与 ASP.NET Core 中间件的前后处理一致。
/// </summary>
internal sealed class InterceptorRun
{
    private readonly IWebApiServerInterceptor[] interceptors;
    private readonly WebApiServerContext context;
    private int completed;

    /// <summary>创建本次请求的执行。</summary>
    /// <param name="interceptors">拦截器（按注册顺序）。</param>
    /// <param name="context">请求上下文。</param>
    public InterceptorRun(IWebApiServerInterceptor[] interceptors, WebApiServerContext context)
    {
        this.interceptors = interceptors;
        this.context = context;
    }

    /// <summary>
    /// 执行请求阶段。返回被拦截器拒绝的详情；没有拒绝时返回 null。拦截器抛出的异常原样向上传播。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>拒绝的详情，或 null。</returns>
    public async Task<InterceptorRefusal?> RunRequestStageAsync(CancellationToken cancellationToken)
    {
        var requesting = new WebApiServerRequestingContext(context);
        for (int i = 0; i < interceptors.Length; i++)
        {
            await interceptors[i].OnRequestAsync(requesting, cancellationToken).ConfigureAwait(false);
            completed = i + 1;
            if (requesting.RejectionDetails is { } rejection)
                return new InterceptorRefusal(rejection.StatusCode, rejection.Code, rejection.Message);
        }

        return null;
    }

    /// <summary>
    /// 执行响应阶段：逆序通知请求阶段已经完成的拦截器。拦截器可以向 <paramref name="headers"/> 追加响应头。
    /// </summary>
    /// <param name="statusCode">将要写出的状态码。</param>
    /// <param name="outcome">结果分类。</param>
    /// <param name="headers">响应头（管线拥有的副本）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行完成的任务。</returns>
    public async Task RunResponseStageAsync(int statusCode, WebApiServerOutcome outcome, IDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var responding = new WebApiServerRespondingContext(context, statusCode, outcome, headers);
        for (int i = completed - 1; i >= 0; i--)
            await interceptors[i].OnRespondingAsync(responding, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 请求阶段被拦截器拒绝的详情（内部）。
/// </summary>
/// <param name="StatusCode">HTTP 状态码（4xx 或 5xx）。</param>
/// <param name="Code">业务码。</param>
/// <param name="Message">英文消息。</param>
internal sealed record InterceptorRefusal(int StatusCode, string Code, string Message);
