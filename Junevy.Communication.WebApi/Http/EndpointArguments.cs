namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// REST 与 SOAP 外观共用的参数规则（内部）：端点名称与每次调用的超时。规则只在这里写一次，两个外观都调用它。
/// </summary>
internal static class EndpointArguments
{
    /// <summary>
    /// 校验显式给出的端点名称。
    /// </summary>
    /// <param name="name">名称。</param>
    /// <returns>原样返回名称。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空或只有空白。</exception>
    public static string RequireName(string name)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("The endpoint name must not be empty.", nameof(name));

        return name;
    }

    /// <summary>
    /// 检查单次调用覆盖的超时。
    /// </summary>
    /// <param name="timeout">调用的超时（毫秒）；null 表示使用配置值。</param>
    /// <returns>非法时的英文信息；合法为 null。</returns>
    public static string? TimeoutError(int? timeout)
        => timeout.HasValue && timeout.Value <= 0 ? "The timeout of a call must be positive." : null;
}
