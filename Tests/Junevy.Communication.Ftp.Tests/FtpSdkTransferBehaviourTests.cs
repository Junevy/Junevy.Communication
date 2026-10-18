using System.Text;
using FluentFTP;
using FluentFTP.Exceptions;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// FluentFTP 的传输行为钉住（P6-C14；第 18.2 节 F21–F27）：直接调用 SDK，断言它在上传、下载与失败时的实际表现。
/// 适配层依赖这些行为（例如是否忽略流的当前位置、失败时从哪里读出应答码），SDK 升级改变行为时这些测试先失败。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpSdkTransferBehaviourTests
{
    [Fact]
    public async Task UploadFile_Skip_ReturnsSkipped_WithoutSendingStor()
    {
        using var local = new TempDirectory();
        string source = WriteLocal(local, "up.bin");
        await using FakeFtpServer server = FtpFixtures.Start();
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        FtpStatus status = await sdk.UploadFile(source, "/docs/recipe.txt", FtpRemoteExists.Skip, false, FtpVerify.None, null, CancellationToken.None);

        Assert.Equal(FtpStatus.Skipped, status);
        Assert.DoesNotContain(server.Commands.Snapshot(), c => c.Verb == "STOR");
    }

    [Fact]
    public async Task UploadFile_Overwrite_DeletesTheRemoteFileBeforeStor()
    {
        // 覆盖时 SDK 先删除远程文件再上传：中途失败时远程文件会消失（F22，README 写明上传不是原子的）。
        using var local = new TempDirectory();
        string source = WriteLocal(local, "up.bin");
        await using FakeFtpServer server = FtpFixtures.Start();
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        await sdk.UploadFile(source, "/docs/recipe.txt", FtpRemoteExists.Overwrite, false, FtpVerify.None, null, CancellationToken.None);

        List<string> verbs = server.Commands.Snapshot().Select(c => c.Verb).ToList();
        Assert.True(verbs.IndexOf("DELE") >= 0, "the SDK did not send DELE");
        Assert.True(verbs.IndexOf("DELE") < verbs.IndexOf("STOR"), "DELE must come before STOR");
    }

    [Fact]
    public async Task UploadStream_IgnoresThePositionOfASeekableStream()
    {
        // 流的当前位置被忽略，从 0 开始读（F23）：本库因此用只向前读的视图包住调用方的流。
        await using FakeFtpServer server = FtpFixtures.Start();
        await using AsyncFtpClient sdk = await ConnectAsync(server);
        using var source = new MemoryStream(Encoding.ASCII.GetBytes("XXXhello")) { Position = 3 };

        await sdk.UploadStream(source, "/docs/stream.txt", FtpRemoteExists.Overwrite, false, null, CancellationToken.None);

        Assert.Equal("XXXhello", Encoding.ASCII.GetString(server.FileSystem.ReadFile("/docs/stream.txt")));
    }

    [Fact]
    public async Task UploadStream_DoesNotDisposeTheStream()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using AsyncFtpClient sdk = await ConnectAsync(server);
        using var source = new MemoryStream(Encoding.ASCII.GetBytes("hello"));

        await sdk.UploadStream(source, "/docs/stream.txt", FtpRemoteExists.Overwrite, false, null, CancellationToken.None);

        Assert.Equal(5, source.Length);
    }

    [Fact]
    public async Task DownloadFile_Aborted_ReturnsFailed_AndTheLastReplyIs426()
    {
        // 中断时 DownloadFile 返回 Failed 而不抛异常，应答码只能从 LastReply 读出（F24）。
        using var local = new TempDirectory();
        await using FakeFtpServer server = FtpFixtures.Start();
        server.Faults.AbortTransfersAfter(5);
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        FtpStatus status = await sdk.DownloadFile(local.File("abort.txt"), "/docs/recipe.txt", FtpLocalExists.Overwrite, FtpVerify.None, null, CancellationToken.None);

        Assert.Equal(FtpStatus.Failed, status);
        Assert.Equal("426", sdk.LastReply.Code);
    }

    [Fact]
    public async Task DownloadStream_Aborted_ReturnsFalse_AndTheLastReplyIs426()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        server.Faults.AbortTransfersAfter(5);
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        bool ok = await sdk.DownloadStream(new MemoryStream(), "/docs/recipe.txt", 0, null, CancellationToken.None, 0);

        Assert.False(ok);
        Assert.Equal("426", sdk.LastReply.Code);
    }

    [Fact]
    public async Task DownloadFile_Missing_ThrowsFtpMissingObjectException_WhoseInnerExceptionCarries550()
    {
        using var local = new TempDirectory();
        await using FakeFtpServer server = FtpFixtures.Start();
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        FtpMissingObjectException ex = await Assert.ThrowsAsync<FtpMissingObjectException>(
            () => sdk.DownloadFile(local.File("none.txt"), "/docs/none.txt", FtpLocalExists.Overwrite, FtpVerify.None, null, CancellationToken.None));

        Assert.Equal("550", Assert.IsType<FtpCommandException>(ex.InnerException).CompletionCode);
    }

    [Fact]
    public async Task UploadFile_StorRepliedWith552_ThrowsFtpException_WhoseInnerExceptionCarries552()
    {
        using var local = new TempDirectory();
        string source = WriteLocal(local, "up.bin");
        await using FakeFtpServer server = FtpFixtures.Start();
        server.Faults.FailCommand("STOR", 552);
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        FtpException ex = await Assert.ThrowsAsync<FtpException>(
            () => sdk.UploadFile(source, "/docs/up.bin", FtpRemoteExists.Overwrite, false, FtpVerify.None, null, CancellationToken.None));

        Assert.Equal("552", Assert.IsType<FtpCommandException>(ex.InnerException).CompletionCode);
    }

    [Fact]
    public async Task DownloadFile_WithoutSize_ReportsAnIndeterminateProgress()
    {
        using var local = new TempDirectory();
        await using FakeFtpServer server = FtpFixtures.Start(new FakeFtpOptions { AdvertiseSize = false });
        await using AsyncFtpClient sdk = await ConnectAsync(server);
        var reports = new List<FtpProgress>();

        await sdk.DownloadFile(local.File("recipe.txt"), "/docs/recipe.txt", FtpLocalExists.Overwrite, FtpVerify.None, new Collector(reports), CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Equal(-1, r.Progress));
    }

    private static string WriteLocal(TempDirectory directory, string name)
    {
        string path = directory.File(name);
        File.WriteAllText(path, "0123456789ABCDEFGHIJ", Encoding.ASCII);
        return path;
    }

    private static async Task<AsyncFtpClient> ConnectAsync(FakeFtpServer server)
    {
        var config = new FtpConfig
        {
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            RetryAttempts = 1,
            ValidateAnyCertificate = true,
            ConnectTimeout = 5000,
            ReadTimeout = 2000,
            DataConnectionConnectTimeout = 2000,
            DataConnectionReadTimeout = 2000,
        };
        var sdk = new AsyncFtpClient(LoopbackTcp.Host, "line1", "secret", server.Port, config);
        await sdk.Connect(CancellationToken.None);
        return sdk;
    }

    private sealed class Collector(List<FtpProgress> reports) : IProgress<FtpProgress>
    {
        public void Report(FtpProgress value) => reports.Add(value);
    }
}
