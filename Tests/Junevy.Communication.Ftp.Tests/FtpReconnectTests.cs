using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 断线与超时之后的重连（P63-D3、D4；计划 Task 6 第 1 条）。涉及真实套接字，放入 <c>SocketTiming</c> 集合。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpReconnectTests
{
    [Fact]
    public async Task IdleDrop_BetweenOperations_TheNextOperationReconnectsAndSucceeds()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        CommResult<IReadOnlyList<FtpEntry>> first = await client.ListAsync("/docs");
        Assert.True(first.IsSuccess, first.ToString());

        // 服务器关闭控制连接（例如空闲超时）。SDK 的 IsConnected 仍是 true，只有主动检查才能发现（第 18.2 节 F28）。
        server.DropAllControlConnections();
        CommResult<IReadOnlyList<FtpEntry>> second = await client.ListAsync("/docs");

        Assert.True(second.IsSuccess, second.ToString());
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task ConnectAsync_AfterAnIdleDrop_ReconnectsAndReportsConnected()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        Assert.True((await client.ConnectAsync()).IsSuccess);

        server.DropAllControlConnections();
        CommResult connected = await client.ConnectAsync();

        Assert.True(connected.IsSuccess, connected.ToString());
        Assert.True(client.IsConnected);
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task ConnectionDroppedMidOperation_IsReportedAsTimeout_AndTheNextOperationSucceeds()
    {
        // 操作进行中断线：SDK 不把它报告为连接错误，而是等到读超时后抛出 TimeoutException（第 18.2 节 F30），
        // 库因此返回 Timeout（计划原本写的是 ConnectionClosed，这里按 SDK 的实际行为记录偏差）。
        // 上传时 SDK 会等待最后的 226 应答；列目录时 SDK 在数据读完后即返回，不等待它（F29），因此用上传。
        using var local = new TempDirectory();
        string source = local.File("up.bin");
        File.WriteAllText(source, "0123456789");
        await using FakeFtpServer server = FtpFixtures.Start();
        server.Faults.DropConnectionBeforeTransferReply();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<FtpTransferStatus> interrupted = await client.UploadFileAsync(source, "/docs/mid.bin");
        CommResult<FtpTransferStatus> next = await client.UploadFileAsync(source, "/docs/mid.bin");

        Assert.Equal(CommErrorKind.Timeout, interrupted.ErrorKind);
        Assert.True(next.IsSuccess, next.ToString());
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task ReplyDelayedBeyondReadTimeout_IsTimeout_AndTheNextOperationSucceeds()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        server.Faults.DelayReply("MLSD", TimeSpan.FromSeconds(1));
        FtpClientOptions options = FtpTestClients.OptionsFor(server);
        options.ReadTimeout = 300;
        await using FtpClient client = FtpClient.Create(options);

        CommResult<IReadOnlyList<FtpEntry>> slow = await client.ListAsync("/docs");
        CommResult<IReadOnlyList<FtpEntry>> next = await client.ListAsync("/docs");

        Assert.Equal(CommErrorKind.Timeout, slow.ErrorKind);
        Assert.True(next.IsSuccess, next.ToString());
    }

    [Fact]
    public async Task DisconnectAsync_ThenAnOperation_ReconnectsOnDemand()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        Assert.True((await client.ListAsync("/docs")).IsSuccess);
        await client.DisconnectAsync();

        CommResult<IReadOnlyList<FtpEntry>> next = await client.ListAsync("/docs");

        Assert.True(next.IsSuccess, next.ToString());
        Assert.Equal(2, server.ConnectionCount);
    }
}
