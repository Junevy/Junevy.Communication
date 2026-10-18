using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 下载（P63-D5、D6、D7、D11；计划 Task 5 第 1 条）。涉及真实套接字，放入 <c>SocketTiming</c> 集合。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpDownloadTests
{
    [Fact]
    public async Task DownloadFile_WritesTheContent()
    {
        using var local = new TempDirectory();
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<FtpTransferStatus> result = await client.DownloadFileAsync("/docs/recipe.txt", local.File("recipe.txt"));

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(FtpTransferStatus.Transferred, result.Data);
        Assert.Equal("hello world!", File.ReadAllText(local.File("recipe.txt"), Encoding.ASCII));
    }

    [Fact]
    public async Task DownloadFile_CreatesTheMissingLocalDirectory()
    {
        using var local = new TempDirectory();
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        string target = local.File(Path.Combine("nested", "dir", "recipe.txt"));

        CommResult<FtpTransferStatus> result = await client.DownloadFileAsync("/docs/recipe.txt", target);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal("hello world!", File.ReadAllText(target, Encoding.ASCII));
    }

    [Fact]
    public async Task DownloadFile_Skip_WhenTheLocalFileExists_IsSkipped_AndKeepsIt()
    {
        using var local = new TempDirectory();
        string target = local.File("recipe.txt");
        File.WriteAllText(target, "local", Encoding.ASCII);
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<FtpTransferStatus> result = await client.DownloadFileAsync("/docs/recipe.txt", target, FtpExistsMode.Skip);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(FtpTransferStatus.Skipped, result.Data);
        Assert.Equal("local", File.ReadAllText(target, Encoding.ASCII));
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task DownloadFile_TransferAbortedMidway_KeepsTheOldFile_AndLeavesNoPartFile()
    {
        // 下载是原子的（P63-D7，Q43）：先写到同目录的临时文件，成功后才替换目标。
        using var local = new TempDirectory();
        string target = local.File("recipe.txt");
        File.WriteAllText(target, "old", Encoding.ASCII);
        await using FakeFtpServer server = FtpFixtures.Start();
        server.Faults.AbortTransfersAfter(5);
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<FtpTransferStatus> result = await client.DownloadFileAsync("/docs/recipe.txt", target);

        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Equal(426, result.ProtocolErrorCode);
        Assert.Equal("old", File.ReadAllText(target, Encoding.ASCII));
        Assert.Empty(Directory.GetFiles(local.Root, "*.part"));
        Assert.Equal(new[] { target }, Directory.GetFiles(local.Root));
    }

    [Fact]
    public async Task DownloadFile_Missing_IsRemoteError_WithCode550()
    {
        using var local = new TempDirectory();
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<FtpTransferStatus> result = await client.DownloadFileAsync("/docs/none.txt", local.File("none.txt"));

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(550, result.ProtocolErrorCode);
        Assert.False(File.Exists(local.File("none.txt")));
    }

    [Fact]
    public async Task DownloadStream_WritesTheContent()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        using var destination = new MemoryStream();

        CommResult result = await client.DownloadAsync("/docs/recipe.txt", destination);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal("hello world!", Encoding.ASCII.GetString(destination.ToArray()));
    }

    [Fact]
    public async Task DownloadStream_DoesNotDisposeTheCallersStream()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        using var destination = new MemoryStream();

        await client.DownloadAsync("/docs/recipe.txt", destination);

        destination.WriteByte(0x21);
        Assert.Equal(13, destination.Length);
    }

    [Fact]
    public async Task DownloadStream_Missing_IsRemoteError_WithCode550()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult result = await client.DownloadAsync("/docs/none.txt", new MemoryStream());

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(550, result.ProtocolErrorCode);
    }

    [Fact]
    public async Task DownloadFile_WithoutSize_ReportsIndeterminateProgress_WithoutPercent()
    {
        using var local = new TempDirectory();
        await using FakeFtpServer server = FtpFixtures.Start(new FakeFtpOptions { AdvertiseSize = false });
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        var progress = new TransferProgressRecorder();

        CommResult<FtpTransferStatus> result = await client.DownloadFileAsync("/docs/recipe.txt", local.File("recipe.txt"), progress: progress);

        Assert.True(result.IsSuccess, result.ToString());
        IReadOnlyList<FtpTransferProgress> reports = progress.Snapshot();
        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Null(r.Percent));
        Assert.Equal(12, reports[^1].TransferredBytes);
    }

    [Fact]
    public async Task LargeFile_5MiB_UploadThenDownload_IsByteIdentical()
    {
        const int size = 5 * 1024 * 1024;
        var bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        using var local = new TempDirectory();
        string source = local.File("big.bin");
        File.WriteAllBytes(source, bytes);
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        var uploadProgress = new TransferProgressRecorder();

        CommResult<FtpTransferStatus> uploaded = await client.UploadFileAsync(source, "/docs/big.bin", progress: uploadProgress);
        CommResult<FtpTransferStatus> downloaded = await client.DownloadFileAsync("/docs/big.bin", local.File("copy.bin"));

        Assert.True(uploaded.IsSuccess, uploaded.ToString());
        Assert.True(downloaded.IsSuccess, downloaded.ToString());
        Assert.Equal(bytes, File.ReadAllBytes(local.File("copy.bin")));
        IReadOnlyList<FtpTransferProgress> reports = uploadProgress.Snapshot();
        Assert.Equal(size, reports[^1].TransferredBytes);
        Assert.Equal(reports.OrderBy(r => r.TransferredBytes).Select(r => r.TransferredBytes), reports.Select(r => r.TransferredBytes));
    }
}
