namespace Junevy.Communication.WebApi.Auth;

/// <summary>
/// 内置认证拦截器（内部）：每次发送尝试前取一个凭据值写入指定的请求头，并把该头标记为敏感。
/// Basic、Bearer 与 ApiKey 只在"头名"和"取值方式"上不同，因此共用这一份实现。
/// </summary>
internal sealed class HeaderAuthInterceptor : WebApiInterceptor
{
    private readonly string headerName;
    private readonly Func<CancellationToken, Task<string>> valueProvider;

    /// <summary>
    /// 创建拦截器。
    /// </summary>
    /// <param name="headerName">写入的请求头名（调用方已校验非空）。</param>
    /// <param name="valueProvider">每次尝试前调用，返回完整的头值（调用方已校验不为 null）。</param>
    public HeaderAuthInterceptor(string headerName, Func<CancellationToken, Task<string>> valueProvider)
    {
        this.headerName = headerName;
        this.valueProvider = valueProvider;
    }

    /// <inheritdoc />
    public override async Task OnSendingAsync(WebApiSendingContext context, CancellationToken cancellationToken)
    {
        string value = await valueProvider(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException($"The credential provider for the '{headerName}' header returned an empty value.");

        context.Headers[headerName] = value;
        context.MarkSensitiveHeader(headerName);
    }
}
