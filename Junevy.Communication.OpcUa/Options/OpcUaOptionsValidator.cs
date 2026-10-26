namespace Junevy.Communication.OpcUa;

/// <summary>
/// <see cref="OpcUaClientOptions"/> 与 <see cref="OpcUaSubscriptionOptions"/> 的校验（计划第 6 节第 1 条）。
/// 错误消息写明属性名与允许范围，参数名统一写成 <c>options.Xxx</c>。
/// </summary>
internal static class OpcUaOptionsValidator
{
    /// <summary>校验客户端选项。</summary>
    /// <param name="options">调用方的选项对象。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException">某项不合法。</exception>
    public static void Validate(OpcUaClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.EndpointUrl))
        {
            throw new ArgumentException("A client must have an endpoint url.", "options.EndpointUrl");
        }

        if (!Uri.TryCreate(options.EndpointUrl, UriKind.Absolute, out Uri? endpoint)
            || !string.Equals(endpoint.Scheme, "opc.tcp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(endpoint.Host))
        {
            throw new ArgumentException(
                $"'{options.EndpointUrl}' is not an absolute opc.tcp url, for example opc.tcp://host:4840/.",
                "options.EndpointUrl");
        }

        if (string.IsNullOrWhiteSpace(options.ApplicationName))
        {
            throw new ArgumentException("The application name must not be empty.", "options.ApplicationName");
        }

        if (options.ApplicationUri is not null && string.IsNullOrWhiteSpace(options.ApplicationUri))
        {
            throw new ArgumentException("The application uri must be null or a non-empty urn.", "options.ApplicationUri");
        }

        if (string.IsNullOrWhiteSpace(options.PkiRoot))
        {
            throw new ArgumentException("The pki root must not be empty.", "options.PkiRoot");
        }

        // null 表示匿名；给了用户名就不能是空白。只给密码不给用户名多半是配置写漏了，不悄悄按匿名处理。
        if (options.UserName is not null && string.IsNullOrWhiteSpace(options.UserName))
        {
            throw new ArgumentException("The user name must be null for anonymous login or a non-empty name.", "options.UserName");
        }

        if (options.UserName is null && options.Password is not null)
        {
            throw new ArgumentException("A password needs a user name; leave both null for anonymous login.", "options.Password");
        }

        Require(options.ConnectTimeout, "options.ConnectTimeout");
        Require(options.OperationTimeout, "options.OperationTimeout");
        Require(options.SessionTimeout, "options.SessionTimeout");
        Require(options.KeepAliveInterval, "options.KeepAliveInterval");
        Require(options.ReconnectInterval, "options.ReconnectInterval");
        Require(options.MaxReconnectInterval, "options.MaxReconnectInterval");

        if (options.MaxReconnectInterval < options.ReconnectInterval)
        {
            throw new ArgumentException(
                $"The maximum reconnect interval ({options.MaxReconnectInterval} ms) must not be below the initial one ({options.ReconnectInterval} ms).",
                "options.MaxReconnectInterval");
        }
    }

    /// <summary>校验订阅选项。</summary>
    /// <param name="options">调用方的选项对象。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException">某项不合法。</exception>
    public static void Validate(OpcUaSubscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PublishingInterval <= 0)
        {
            throw new ArgumentException(
                $"The publishing interval must be greater than 0, but is {options.PublishingInterval} ms.",
                "options.PublishingInterval");
        }

        // null 表示与发布间隔相同；0 表示服务器支持的最快速度；因此只拒绝负数。
        if (options.SamplingInterval < 0)
        {
            throw new ArgumentException(
                $"The sampling interval must be null or at least 0, but is {options.SamplingInterval.Value} ms.",
                "options.SamplingInterval");
        }

        if (options.QueueSize < 1)
        {
            throw new ArgumentException($"The queue size must be at least 1, but is {options.QueueSize}.", "options.QueueSize");
        }
    }

    private static void Require(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentException($"The interval must be greater than 0, but is {value} ms.", name);
        }
    }
}
