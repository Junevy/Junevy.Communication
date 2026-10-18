using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 显式与隐式 FTPS（P63-D13；计划 Task 3 第 2 条）。涉及真实套接字，放入 <c>SocketTiming</c> 集合。
/// 数据连接的加密断言在 Task 4 完成（列目录是第一个打开数据连接的操作）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpTlsTests
{
    [Fact]
    public async Task ExplicitTls_WithUntrustedCertificatesAllowed_Succeeds_AndSendsProt()
    {
        await using FakeFtpServer server = FakeFtpServer.Start(new FakeFtpOptions { Tls = FakeFtpTls.Explicit });
        FtpClientOptions options = FtpTestClients.OptionsFor(server, encryption: FtpEncryption.Explicit, allowUntrusted: true);
        await using FtpClient client = FtpClient.Create(options);

        CommResult result = await client.ConnectAsync();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(client.IsConnected);
        Assert.Contains(server.Commands.Snapshot(), c => c.Verb == "AUTH" && c.Argument == "TLS");
        Assert.Contains(server.Commands.Snapshot(), c => c.Verb == "PROT" && c.Argument == "P");
    }

    [Fact]
    public async Task ImplicitTls_WithUntrustedCertificatesAllowed_Succeeds()
    {
        await using FakeFtpServer server = FakeFtpServer.Start(new FakeFtpOptions { Tls = FakeFtpTls.Implicit });
        FtpClientOptions options = FtpTestClients.OptionsFor(server, encryption: FtpEncryption.Implicit, allowUntrusted: true);
        await using FtpClient client = FtpClient.Create(options);

        CommResult result = await client.ConnectAsync();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(client.IsConnected);
        Assert.Contains(server.Commands.Snapshot(), c => c.Verb == "PROT" && c.Argument == "P");
    }

    [Fact]
    public async Task ExplicitTls_UntrustedCertificate_IsAuthenticationFailed()
    {
        await using FakeFtpServer server = FakeFtpServer.Start(new FakeFtpOptions { Tls = FakeFtpTls.Explicit });
        FtpClientOptions options = FtpTestClients.OptionsFor(server, encryption: FtpEncryption.Explicit, allowUntrusted: false);
        await using FtpClient client = FtpClient.Create(options);

        CommResult result = await client.ConnectAsync();

        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ImplicitTls_UntrustedCertificate_IsAuthenticationFailed()
    {
        await using FakeFtpServer server = FakeFtpServer.Start(new FakeFtpOptions { Tls = FakeFtpTls.Implicit });
        FtpClientOptions options = FtpTestClients.OptionsFor(server, encryption: FtpEncryption.Implicit, allowUntrusted: false);
        await using FtpClient client = FtpClient.Create(options);

        CommResult result = await client.ConnectAsync();

        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.False(client.IsConnected);
    }
}
