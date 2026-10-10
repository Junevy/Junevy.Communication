using System.Net.Security;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// TLS 测试的共享辅助：启用 TLS 的客户端配置、按指纹钉住的校验回调，以及把非泛型日志器适配为 <see cref="ILogger{TCategoryName}"/>。
/// </summary>
internal static class TlsTestHelpers
{
    /// <summary>
    /// 连接到回环服务端、启用 TLS 的客户端配置（按行分帧，连接超时 10000 毫秒）。
    /// </summary>
    /// <param name="port">服务端端口。</param>
    /// <param name="configure">对 TLS 选项的额外设置；可为 null。</param>
    /// <param name="handshakeTimeout">握手总时限（毫秒）。</param>
    /// <returns>客户端配置。</returns>
    public static TcpClientChannelConfig CreateTlsClientConfig(int port, Action<TcpClientTlsOptions>? configure = null, int handshakeTimeout = 5000)
    {
        var config = new TcpClientChannelConfig
        {
            Host = "127.0.0.1",
            Port = port,
            ConnectTimeout = 10000,
            HandshakeTimeout = handshakeTimeout,
            Framing = TcpTestHelpers.LineFraming(),
        };
        config.Tls.Enabled = true;
        configure?.Invoke(config.Tls);
        return config;
    }

    /// <summary>
    /// 只接受指纹等于 <paramref name="thumbprint"/> 的证书（忽略其他校验结果），即"钉住"一张测试证书。
    /// </summary>
    /// <param name="thumbprint">期望的指纹。</param>
    /// <returns>校验回调。</returns>
    public static RemoteCertificateValidationCallback Pin(string thumbprint)
        => (sender, certificate, chain, errors) =>
            certificate != null && string.Equals(certificate.GetCertHashString(), thumbprint, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 把非泛型日志器适配为 <see cref="ILogger{TCategoryName}"/>（测试用），让 <see cref="TestLogger"/> 能注入到需要泛型日志器的构造函数中。
/// </summary>
/// <typeparam name="T">类别类型。</typeparam>
internal sealed class CategoryLogger<T> : ILogger<T>
{
    private readonly ILogger inner;

    /// <summary>创建适配器。</summary>
    /// <param name="inner">被适配的日志器。</param>
    public CategoryLogger(ILogger inner)
    {
        this.inner = inner;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => inner.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => inner.Log(logLevel, eventId, state, exception, formatter);
}
