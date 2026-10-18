using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 显式与隐式 FTPS（P63-D13；计划 Task 3 第 2 条）。涉及真实套接字，放入 <c>SocketTiming</c> 集合。
/// 模拟服务器在 <c>PROT P</c> 之后只用 TLS 收发数据，因此 FTPS 下的列目录与传输成功，就说明数据连接是加密的（P6.3 审阅 R3）。
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

    [Theory]
    [InlineData(FakeFtpTls.Explicit, FtpEncryption.Explicit)]
    [InlineData(FakeFtpTls.Implicit, FtpEncryption.Implicit)]
    public async Task Tls_ListUploadAndDownload_RunOverEncryptedDataConnections(FakeFtpTls tls, FtpEncryption encryption)
    {
        using var local = new TempDirectory();
        string source = local.File("up.txt");
        File.WriteAllText(source, "tls-data");
        await using FakeFtpServer server = FtpFixtures.Start(new FakeFtpOptions { Tls = tls });
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server, encryption: encryption, allowUntrusted: true));

        CommResult<IReadOnlyList<FtpEntry>> list = await client.ListAsync("/docs");
        CommResult<FtpTransferStatus> uploaded = await client.UploadFileAsync(source, "/docs/up.txt");
        CommResult<FtpTransferStatus> downloaded = await client.DownloadFileAsync("/docs/recipe.txt", local.File("copy.txt"));

        Assert.True(list.IsSuccess, list.ToString());
        Assert.Contains(list.Data!, e => e.Name == "recipe.txt");
        Assert.True(uploaded.IsSuccess, uploaded.ToString());
        Assert.Equal("tls-data", System.Text.Encoding.ASCII.GetString(server.FileSystem.ReadFile("/docs/up.txt")));
        Assert.True(downloaded.IsSuccess, downloaded.ToString());
        Assert.Equal("hello world!", File.ReadAllText(local.File("copy.txt")));
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
