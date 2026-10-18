namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// 服务端拦截器的基类：两个阶段的方法都是空实现，只需重写关心的阶段。
/// </summary>
public abstract class WebApiServerInterceptor : IWebApiServerInterceptor
{
    /// <inheritdoc />
    public virtual Task OnRequestAsync(WebApiServerRequestingContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnRespondingAsync(WebApiServerRespondingContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
