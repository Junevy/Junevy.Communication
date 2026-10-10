using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using static Junevy.Communication.Tcp.Tests.TcpTestHelpers;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// TCP 客户端通道的配置校验与快照语义（计划 10.3 的 <c>Config_Invalid_Throws</c>、<c>Config_NotMutated</c>、
/// <c>Tls_EnabledBeforeTask11_ReturnsNotSupported</c>）。本类不建立套接字连接。
/// </summary>
public sealed class TcpClientChannelConfigTests
{
    [Theory(Timeout = 30000)]
    [InlineData("Port", 0)]
    [InlineData("Port", 65536)]
    [InlineData("Host", "")]
    [InlineData("Host", "   ")]
    [InlineData("ConnectTimeout", 0)]
    [InlineData("ConnectTimeout", -1)]
    [InlineData("SendTimeout", 0)]
    [InlineData("SendTimeout", -1)]
    [InlineData("RequestTimeout", 0)]
    [InlineData("RequestTimeout", -1)]
    [InlineData("HandshakeTimeout", -1)]
    [InlineData("IdleTimeout", -1)]
    [InlineData("PartialFrameTimeout", -1)]
    [InlineData("DisconnectTimeout", -1)]
    [InlineData("LocalPort", -1)]
    [InlineData("LocalPort", 65536)]
    [InlineData("LocalAddress", "not-an-address")]
    [InlineData("LateReplyWindow", -2)]
    [InlineData("Socket.ReceiveBufferSize", -1)]
    [InlineData("Socket.SendBufferSize", -1)]
    [InlineData("Socket.LingerTime", -2)]
    [InlineData("Socket.KeepAlive.Time", 0)]
    [InlineData("Socket.KeepAlive.Interval", 0)]
    [InlineData("Socket.KeepAlive.RetryCount", 0)]
    [InlineData("Heartbeat.Payload", null)]
    [InlineData("Framing", null)]
    public void Config_Invalid_Throws(string property, object? value)
    {
        var config = CreateConfig(5000);
        ApplyInvalid(config, property, value);

        Assert.ThrowsAny<ArgumentException>(() => new TcpClientChannel(config).Dispose());
    }

    [Fact(Timeout = 30000)]
    public void Config_NotMutated()
    {
        var config = new TcpClientChannelConfig
        {
            Host = "localhost",
            Port = 5000,
            LocalAddress = "127.0.0.1",
            LocalPort = 0,
            ConnectTimeout = 3000,
            HandshakeTimeout = 4000,
            SendTimeout = 2500,
            RequestTimeout = 1500,
            IdleTimeout = 9000,
            PartialFrameTimeout = 100,
            DisconnectTimeout = 700,
            Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\r\n", "hex:0A" }, KeepDelimiter = true },
            Correlation = CorrelationMode.Sequential,
            ResetOnRequestTimeout = false,
            LateReplyWindow = 250,
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 1000, Timeout = 500, MaxFailures = 4, OnlyWhenIdle = false, Payload = "PING", ExpectedReply = "PONG" },
            Reconnect = new ReconnectOptions { Enabled = true, Mode = ReconnectMode.FixedInterval, Interval = 250, MaxInterval = 5000, MaxAttempts = 7, OnInitialFailure = true },
            Socket = new TcpSocketOptions
            {
                NoDelay = false,
                ReceiveBufferSize = 8192,
                SendBufferSize = 8192,
                LingerTime = 0,
                KeepAlive = new TcpKeepAliveOptions { Enabled = true, Time = 12000, Interval = 3000, RetryCount = 5 },
            },
            Tls = new TcpClientTlsOptions
            {
                Enabled = false,
                TargetHost = "example.test",
                Protocols = SslProtocols.Tls12,
                CheckCertificateRevocation = false,
                AllowUntrustedServerCertificate = true,
                ClientCertificate = new CertificateSource { Thumbprint = "ABCDEF", PfxPasswordEnvironmentVariable = "PFX_PASSWORD" },
            },
            ReceiveQueueCapacity = 64,
            QueueFullMode = QueueFullMode.DropOldest,
        };

        string before = Describe(config);
        using (var channel = new TcpClientChannel(config, null, new TcpChannelComponents()))
        {
            Assert.Same(config, channel.Config);
        }

        Assert.Equal(before, Describe(config));
    }

    [Fact(Timeout = 30000)]
    public async Task Tls_EnabledBeforeTask11_ReturnsNotSupported()
    {
        // Task 11 实现 TLS 时删除本测试。
        var config = CreateConfig(FreePort());
        config.Tls = new TcpClientTlsOptions { Enabled = true };
        await using var channel = new TcpClientChannel(config);

        CommResult result = await WithinAsync(channel.ConnectAsync(), 5000);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.NotSupported, result.ErrorKind);
        Assert.False(channel.IsTlsActive);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
    }

    // 按属性名把配置改成非法值。每个用例只改一处，其余保持合法。
    private static void ApplyInvalid(TcpClientChannelConfig config, string property, object? value)
    {
        switch (property)
        {
            case "Port":
                config.Port = (int)value!;
                break;
            case "Host":
                config.Host = (string)value!;
                break;
            case "ConnectTimeout":
                config.ConnectTimeout = (int)value!;
                break;
            case "SendTimeout":
                config.SendTimeout = (int)value!;
                break;
            case "RequestTimeout":
                config.RequestTimeout = (int)value!;
                break;
            case "HandshakeTimeout":
                config.HandshakeTimeout = (int)value!;
                break;
            case "IdleTimeout":
                config.IdleTimeout = (int)value!;
                break;
            case "PartialFrameTimeout":
                config.PartialFrameTimeout = (int)value!;
                break;
            case "DisconnectTimeout":
                config.DisconnectTimeout = (int)value!;
                break;
            case "LocalPort":
                config.LocalPort = (int)value!;
                break;
            case "LocalAddress":
                config.LocalAddress = (string)value!;
                break;
            case "LateReplyWindow":
                config.LateReplyWindow = (int)value!;
                break;
            case "Socket.ReceiveBufferSize":
                config.Socket.ReceiveBufferSize = (int)value!;
                break;
            case "Socket.SendBufferSize":
                config.Socket.SendBufferSize = (int)value!;
                break;
            case "Socket.LingerTime":
                config.Socket.LingerTime = (int)value!;
                break;
            case "Socket.KeepAlive.Time":
                config.Socket.KeepAlive.Time = (int)value!;
                break;
            case "Socket.KeepAlive.Interval":
                config.Socket.KeepAlive.Interval = (int)value!;
                break;
            case "Socket.KeepAlive.RetryCount":
                config.Socket.KeepAlive.RetryCount = (int)value!;
                break;
            case "Heartbeat.Payload":
                // 启用心跳却没有探测负载：由基类的 StreamClientOptions 拒绝。
                config.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 100, Payload = null };
                break;
            case "Framing":
                config.Framing = null!;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(property), property, "Unknown property.");
        }
    }

    // 递归输出对象的全部公共属性值，用于断言构造前后配置对象没有被改写。
    private static string Describe(object? value, int depth = 0)
    {
        if (value == null)
            return "null";
        if (depth > 6)
            return "...";

        Type type = value.GetType();
        if (value is string || type.IsPrimitive || type.IsEnum)
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";

        if (value is Array array)
            return "[" + string.Join(",", array.Cast<object?>().Select(item => Describe(item, depth + 1))) + "]";

        var builder = new StringBuilder(type.Name).Append('{');
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
            builder.Append(property.Name).Append('=').Append(Describe(property.GetValue(value), depth + 1)).Append(';');

        return builder.Append('}').ToString();
    }
}
