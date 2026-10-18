using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 流的上传与下载（P63-D6 的流约定、D11；P6.3 审阅 R2 与设计问题 B）：只支持异步的流同样可用；
/// 可定位的流以当前位置为起点，并报告按剩余长度计算的百分比。涉及真实套接字，放入 <c>SocketTiming</c> 集合。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpStreamTransferTests
{
    [Fact]
    public async Task Upload_FromAnAsyncOnlyStream_SendsTheContent_WithoutAPercent()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        using var source = new AsyncOnlyStream(new MemoryStream(Encoding.ASCII.GetBytes("async-only")));
        var progress = new TransferProgressRecorder();

        CommResult<FtpTransferStatus> result = await client.UploadAsync(source, "/docs/async.txt", progress: progress);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal("async-only", Encoding.ASCII.GetString(server.FileSystem.ReadFile("/docs/async.txt")));
        Assert.All(progress.Snapshot(), r => Assert.Null(r.Percent));
    }

    [Fact]
    public async Task Download_ToAnAsyncOnlyStream_WritesTheContent()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        var buffer = new MemoryStream();
        using var destination = new AsyncOnlyStream(buffer);

        CommResult result = await client.DownloadAsync("/docs/recipe.txt", destination);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal("hello world!", Encoding.ASCII.GetString(buffer.ToArray()));
    }

    [Fact]
    public async Task Upload_FromASeekableStream_ReportsAPercent_OverTheRemainingLength()
    {
        const int payload = 300_000;
        var bytes = new byte[3 + payload];
        new Random(7).NextBytes(bytes);
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        using var source = new MemoryStream(bytes) { Position = 3 };
        var progress = new TransferProgressRecorder();

        CommResult<FtpTransferStatus> result = await client.UploadAsync(source, "/docs/seekable.bin", progress: progress);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(bytes.AsSpan(3).ToArray(), server.FileSystem.ReadFile("/docs/seekable.bin"));
        IReadOnlyList<FtpTransferProgress> reports = progress.Snapshot();
        Assert.All(reports, r => Assert.NotNull(r.Percent));
        Assert.Equal(payload, reports[^1].TransferredBytes);
        Assert.Equal(100, reports[^1].Percent!.Value, precision: 3);
    }

    [Fact]
    public async Task Download_ToASeekableStream_WritesFromTheCurrentPosition()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        using var destination = new MemoryStream();
        destination.Write(Encoding.ASCII.GetBytes("ABCD"));
        destination.Position = 2;

        CommResult result = await client.DownloadAsync("/docs/recipe.txt", destination);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal("ABhello world!", Encoding.ASCII.GetString(destination.ToArray()));
        Assert.Equal(14, destination.Position);
    }
}
