using System.Diagnostics;
using System.Net.Sockets;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using static Junevy.Communication.Tcp.Tests.ServerTestHelpers;
using static Junevy.Communication.Tcp.Tests.TcpTestHelpers;
using static Junevy.Communication.Tcp.Tests.TlsTestHelpers;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// TLS（客户端与服务端）的端到端测试（计划 13.3）。证书由 <see cref="TestCertificates"/> 生成，不写入任何证书存储。
/// 时间相关断言使用区间（计划第 1 节第 5 条）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class TcpTlsTests
{
    private readonly ITestOutputHelper output;

    /// <summary>创建测试类（xUnit 注入输出辅助）。</summary>
    public TcpTlsTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact(Timeout = 30000)]
    public async Task Tls_EchoRoundTrip()
    {
        using TestCertificate serverCertificate = TestCertificates.CreateSelfSigned();
        int port = FreePort();
        await using TcpServer server = CreateTlsServer(port, new TcpChannelComponents { ServerCertificate = serverCertificate.Certificate });
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        await using var client = new TcpClientChannel(CreateTlsClientConfig(port),
                                                      components: new TcpChannelComponents { RemoteCertificateValidation = Pin(serverCertificate.Thumbprint) });
        CommResult connected = await WithinAsync(client.ConnectAsync(), 10000);
        Assert.True(connected.IsSuccess, connected.ToString());
        Assert.True(client.IsTlsActive);

        CommResult<byte[]> reply = await WithinAsync(client.RequestAsync(Ascii("PING")), 5000);
        Assert.True(reply.IsSuccess, reply.ToString());
        Assert.Equal(Ascii("PING"), DataOf(reply));

        await WaitUntilAsync(() => server.SessionCount == 1, 5000);
        Assert.True(server.Sessions.Single().IsTlsActive);
    }

    [Fact(Timeout = 30000)]
    public async Task Tls_UntrustedCertificate_AuthenticationFailed()
    {
        // 测试证书是自签名的，默认校验（sslPolicyErrors == None）不接受它。
        using TestCertificate serverCertificate = TestCertificates.CreateSelfSigned();
        int port = FreePort();
        await using TcpServer server = CreateTlsServer(port, new TcpChannelComponents { ServerCertificate = serverCertificate.Certificate });
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        await using var client = new TcpClientChannel(CreateTlsClientConfig(port));

        CommResult result = await WithinAsync(client.ConnectAsync(), 10000);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.False(client.IsTlsActive);
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    [Fact(Timeout = 30000)]
    public async Task Tls_AllowUntrusted_SucceedsAndLogsWarning()
    {
        using TestCertificate serverCertificate = TestCertificates.CreateSelfSigned();
        int port = FreePort();
        await using TcpServer server = CreateTlsServer(port, new TcpChannelComponents { ServerCertificate = serverCertificate.Certificate });
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        var logger = new TestLogger();
        await using var client = new TcpClientChannel(CreateTlsClientConfig(port, tls => tls.AllowUntrustedServerCertificate = true),
                                                      new CategoryLogger<TcpClientChannel>(logger));

        CommResult connected = await WithinAsync(client.ConnectAsync(), 10000);

        Assert.True(connected.IsSuccess, connected.ToString());
        Assert.True(client.IsTlsActive);
        IReadOnlyList<TestLogEntry> warnings = logger.GetEntries(LogLevel.Warning);
        Assert.Contains(warnings, entry => entry.Message.Contains(serverCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase)
                                           && entry.Message.Contains("RemoteCertificateChainErrors"));
    }

    [Fact(Timeout = 30000)]
    public async Task Tls_PinnedThumbprint_Callback()
    {
        using TestCertificate serverCertificate = TestCertificates.CreateSelfSigned();
        int port = FreePort();
        await using TcpServer server = CreateTlsServer(port, new TcpChannelComponents { ServerCertificate = serverCertificate.Certificate });
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        // 指纹匹配：校验通过。
        await using (var matching = new TcpClientChannel(CreateTlsClientConfig(port),
                                                         components: new TcpChannelComponents { RemoteCertificateValidation = Pin(serverCertificate.Thumbprint) }))
        {
            CommResult connected = await WithinAsync(matching.ConnectAsync(), 10000);
            Assert.True(connected.IsSuccess, connected.ToString());
        }

        // 指纹不匹配：校验失败。
        await using var mismatched = new TcpClientChannel(CreateTlsClientConfig(port),
                                                          components: new TcpChannelComponents { RemoteCertificateValidation = Pin("0123456789ABCDEF0123456789ABCDEF01234567") });
        CommResult rejected = await WithinAsync(mismatched.ConnectAsync(), 10000);
        Assert.False(rejected.IsSuccess);
        Assert.Equal(CommErrorKind.AuthenticationFailed, rejected.ErrorKind);
    }

    [Fact(Timeout = 30000)]
    public async Task Tls_MutualRequired_NoClientCert_Fails()
    {
        using TestCertificate serverCertificate = TestCertificates.CreateSelfSigned();
        int port = FreePort();
        var serverLogger = new TestLogger();
        var serverConfig = CreateServerConfig(port);
        serverConfig.Tls.Enabled = true;
        serverConfig.Tls.ClientCertificateRequired = true;
        await using var server = new TcpServer(serverConfig, new CategoryLogger<TcpServer>(serverLogger),
                                               new TcpChannelComponents { ServerCertificate = serverCertificate.Certificate });
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        // 客户端信任服务端证书，但不提供客户端证书。握手完成后的探测应当失败（TLS 1.3 下客户端可能先于服务端完成握手）。
        var probe = new DelegateInitializer(async (view, token) =>
            (await view.RequestAsync(Ascii("PING"), new RequestOptions { Timeout = 2000 }, token)).ToResult());
        await using var client = new TcpClientChannel(CreateTlsClientConfig(port),
                                                      components: new TcpChannelComponents
                                                      {
                                                          Initializer = probe,
                                                          RemoteCertificateValidation = Pin(serverCertificate.Thumbprint),
                                                      });

        CommResult result = await WithinAsync(client.ConnectAsync(), 15000);

        Assert.False(result.IsSuccess, "A client without a certificate must not be connected.");
        await WaitUntilAsync(() => serverLogger.GetEntries(LogLevel.Warning)
                                    .Any(entry => entry.Message.Contains("AuthenticationFailed")), 5000);
        Assert.Equal(0, recorder.ConnectedCount);
    }

    [Fact(Timeout = 30000)]
    public async Task Tls_MutualRequired_UntrustedClientCertificate_Fails()
    {
        // 服务端没有自定义回调：默认规则拒绝校验有错误的客户端证书（自签名、不受信任）。
        using TestCertificate serverCertificate = TestCertificates.CreateSelfSigned();
        using TestCertificate clientCertificate = TestCertificates.CreateSelfSigned("CN=client");
        int port = FreePort();
        var serverLogger = new TestLogger();
        var serverConfig = CreateServerConfig(port);
        serverConfig.Tls.Enabled = true;
        serverConfig.Tls.ClientCertificateRequired = true;
        await using var server = new TcpServer(serverConfig, new CategoryLogger<TcpServer>(serverLogger),
                                               new TcpChannelComponents { ServerCertificate = serverCertificate.Certificate });
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        var probe = new DelegateInitializer(async (view, token) =>
            (await view.RequestAsync(Ascii("PING"), new RequestOptions { Timeout = 2000 }, token)).ToResult());
        await using var client = new TcpClientChannel(CreateTlsClientConfig(port), components: new TcpChannelComponents
        {
            ClientCertificate = clientCertificate.Certificate,
            RemoteCertificateValidation = Pin(serverCertificate.Thumbprint),
            Initializer = probe,
        });

        CommResult result = await WithinAsync(client.ConnectAsync(), 15000);

        Assert.False(result.IsSuccess, "The server must reject an untrusted client certificate.");
        await WaitUntilAsync(() => serverLogger.GetEntries(LogLevel.Warning)
                                    .Any(entry => entry.Message.Contains("AuthenticationFailed")), 5000);
        Assert.Equal(0, recorder.ConnectedCount);
    }

    [Fact(Timeout = 30000)]
    public async Task Tls_MutualRequired_WithClientCert_Succeeds()
    {
        using TestCertificate serverCertificate = TestCertificates.CreateSelfSigned();
        using TestCertificate clientCertificate = TestCertificates.CreateSelfSigned("CN=client");
        int port = FreePort();
        var serverConfig = CreateServerConfig(port);
        serverConfig.Tls.Enabled = true;
        serverConfig.Tls.ClientCertificateRequired = true;
        await using var server = new TcpServer(serverConfig, components: new TcpChannelComponents
        {
            ServerCertificate = serverCertificate.Certificate,
            RemoteCertificateValidation = Pin(clientCertificate.Thumbprint),
        });
        server.FrameReceived += (sender, args) => { _ = server.SendAsync(args.Session.Id, args.Data); };
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        await using var client = new TcpClientChannel(CreateTlsClientConfig(port), components: new TcpChannelComponents
        {
            ClientCertificate = clientCertificate.Certificate,
            RemoteCertificateValidation = Pin(serverCertificate.Thumbprint),
        });

        CommResult connected = await WithinAsync(client.ConnectAsync(), 10000);
        Assert.True(connected.IsSuccess, connected.ToString());
        Assert.True(client.IsTlsActive);

        CommResult<byte[]> reply = await WithinAsync(client.RequestAsync(Ascii("MUTUAL")), 5000);
        Assert.True(reply.IsSuccess, reply.ToString());
        Assert.Equal(Ascii("MUTUAL"), DataOf(reply));
    }

    [Fact(Timeout = 30000)]
    public async Task Tls_HandshakeTimeout_AgainstSilentServer()
    {
        // 服务端接受连接但从不回应 ServerHello：握手时限到期，结果为 Timeout。
        using SilentTcpServer silent = SilentTcpServer.Start();
        await using var client = new TcpClientChannel(CreateTlsClientConfig(silent.Port, handshakeTimeout: 500));

        var stopwatch = Stopwatch.StartNew();
        CommResult result = await WithinAsync(client.ConnectAsync(), 6000);
        stopwatch.Stop();
        output.WriteLine($"TLS handshake timeout observed after {stopwatch.ElapsedMilliseconds} ms.");

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 400d, 2500d);
        Assert.False(client.IsTlsActive);
    }

    [Fact(Timeout = 30000)]
    public async Task Tls_ReconnectRedoesHandshake()
    {
        using TestCertificate serverCertificate = TestCertificates.CreateSelfSigned();
        int port = FreePort();
        await using TcpServer server = CreateTlsServer(port, new TcpChannelComponents { ServerCertificate = serverCertificate.Certificate });
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        await using var client = new TcpClientChannel(CreateTlsClientConfig(port),
                                                      components: new TcpChannelComponents { RemoteCertificateValidation = Pin(serverCertificate.Thumbprint) });
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        Assert.True(client.IsTlsActive);

        await WithinAsync(client.DisconnectAsync(), 5000);
        Assert.False(client.IsTlsActive);

        CommResult reconnected = await WithinAsync(client.ConnectAsync(), 10000);
        Assert.True(reconnected.IsSuccess, reconnected.ToString());
        Assert.True(client.IsTlsActive);

        // 每次连接都重新完成 TLS 握手：服务端为两次连接分别建立了加入会话表的会话。
        await WaitUntilAsync(() => recorder.ConnectedCount == 2, 5000);
        Assert.True(recorder.Connected[1].IsTlsActive);
    }

    [Fact(Timeout = 30000)]
    public async Task TlsServer_SessionHandshakeTimeout()
    {
        // 客户端只建立 TCP 连接，不发送 ClientHello：会话在 SessionHandshakeTimeout 后被关闭，且不触发 SessionConnected。
        using TestCertificate serverCertificate = TestCertificates.CreateSelfSigned();
        int port = FreePort();
        var serverConfig = CreateServerConfig(port);
        serverConfig.Tls.Enabled = true;
        serverConfig.SessionHandshakeTimeout = 500;
        await using var server = new TcpServer(serverConfig, components: new TcpChannelComponents { ServerCertificate = serverCertificate.Certificate });
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        using TcpClient silent = await ConnectRawAsync(port);
        var stopwatch = Stopwatch.StartNew();
        bool closed = await WaitForPeerCloseAsync(silent, 5000);
        stopwatch.Stop();

        Assert.True(closed, "The server did not close the connection that never sent a ClientHello.");
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 400d, 2500d);
        Assert.Equal(0, recorder.ConnectedCount);
        Assert.Equal(0, server.SessionCount);
    }

    [Fact(Timeout = 30000)]
    public void Tls_ClientCertificateWithoutLocator_ThrowsAtConstruction()
    {
        // 既没有指纹也没有 PFX 路径：构造时即为配置错误（D5）。
        var config = CreateTlsClientConfig(FreePort());
        config.Tls.ClientCertificate = new CertificateSource();

        Assert.Throws<ArgumentException>(() => new TcpClientChannel(config));
    }

    [Fact(Timeout = 30000)]
    public void Tls_EnabledWithoutServerCertificate_ThrowsAtConstruction()
    {
        var config = CreateServerConfig(FreePort());
        config.Tls.Enabled = true;

        Assert.Throws<ArgumentException>(() => new TcpServer(config));
    }

    // 启用 TLS 的回显服务端：每帧原样返回。
    private static TcpServer CreateTlsServer(int port, TcpChannelComponents components)
    {
        var config = CreateServerConfig(port);
        config.Tls.Enabled = true;
        var server = new TcpServer(config, components: components);
        server.FrameReceived += (sender, args) => { _ = server.SendAsync(args.Session.Id, args.Data); };
        return server;
    }
}
