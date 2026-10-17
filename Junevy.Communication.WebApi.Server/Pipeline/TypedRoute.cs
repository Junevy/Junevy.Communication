using Junevy.Communication.WebApi.Server.Formatting;

namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 类型化的路由（内部）：把宿主声明的强类型处理器适配为 <see cref="IRoute"/>，返回值在这里格式化。
/// </summary>
/// <typeparam name="TResponse">处理器返回的数据类型。</typeparam>
internal sealed class TypedRoute<TResponse> : IRoute
{
    private readonly Func<object?, WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> invoke;
    private readonly ResponseFormatter formatter;

    /// <summary>创建路由。</summary>
    /// <param name="requestType">请求正文的类型；没有正文时为 null。</param>
    /// <param name="invoke">把擦除类型的请求转发给强类型处理器的委托。</param>
    /// <param name="formatter">响应格式化器。</param>
    public TypedRoute(Type? requestType, Func<object?, WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> invoke, ResponseFormatter formatter)
    {
        RequestType = requestType;
        this.invoke = invoke;
        this.formatter = formatter;
    }

    /// <inheritdoc />
    public Type? RequestType { get; }

    /// <inheritdoc />
    public async Task<ServerResult> InvokeAsync(object? request, WebApiServerContext context, CancellationToken cancellationToken)
    {
        WebApiServerResponse<TResponse>? response = await invoke(request, context, cancellationToken).ConfigureAwait(false);
        if (response == null)
            throw new InvalidOperationException("The route handler returned null instead of a WebApiServerResponse.");

        ServerResponse formatted = formatter.Format(response);
        if (response.IsSuccess)
            return new ServerResult(formatted.StatusCode, formatted, WebApiServerOutcome.Succeeded);

        return new ServerResult(formatted.StatusCode, formatted, WebApiServerOutcome.BusinessFailure, response.Code, response.Message);
    }
}
