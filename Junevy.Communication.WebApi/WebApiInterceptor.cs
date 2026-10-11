namespace Junevy.Communication.WebApi;

/// <summary>
/// <see cref="IWebApiInterceptor"/> 的便捷基类：两个方法默认什么也不做，只需覆盖关心的那一个（net472 没有默认接口成员）。
/// </summary>
public abstract class WebApiInterceptor : IWebApiInterceptor
{
    /// <inheritdoc />
    public virtual Task OnSendingAsync(WebApiSendingContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnReceivedAsync(WebApiReceivedContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
