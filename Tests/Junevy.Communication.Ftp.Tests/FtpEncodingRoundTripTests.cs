using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 文件名编码（P63-D12；计划 Task 5 第 1 条）：GBK 服务器上的中文文件名能列出、下载、上传且名称一致；
/// 默认设置下 UTF-8 中文文件名同样往返一致。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpEncodingRoundTripTests
{
    private const string ChineseName = "配方1.txt";

    [Fact]
    public async Task Gbk_UploadListDownload_RoundTripsTheChineseName()
    {
        Encoding gbk = FtpFixtures.Gbk();
        using var local = new TempDirectory();
        string source = local.File("source.txt");
        File.WriteAllText(source, "gbk content", Encoding.ASCII);
        await using FakeFtpServer server = FtpFixtures.Start(new FakeFtpOptions { AdvertiseUtf8 = false, FileNameEncoding = gbk });
        FtpClientOptions options = FtpTestClients.OptionsFor(server);
        options.EncodingName = "GBK";
        await using FtpClient client = FtpClient.Create(options);

        CommResult<FtpTransferStatus> uploaded = await client.UploadFileAsync(source, "/" + ChineseName);
        CommResult<IReadOnlyList<FtpEntry>> listed = await client.ListAsync("/");
        CommResult<FtpTransferStatus> downloaded = await client.DownloadFileAsync("/" + ChineseName, local.File("copy.txt"));

        Assert.True(uploaded.IsSuccess, uploaded.ToString());
        Assert.True(listed.IsSuccess, listed.ToString());
        Assert.Contains(listed.Data!, e => e.Name == ChineseName);
        Assert.True(downloaded.IsSuccess, downloaded.ToString());
        Assert.Equal("gbk content", File.ReadAllText(local.File("copy.txt"), Encoding.ASCII));
    }

    [Fact]
    public async Task Utf8_UploadListDownload_RoundTripsTheChineseName_WithTheDefaults()
    {
        using var local = new TempDirectory();
        string source = local.File("source.txt");
        File.WriteAllText(source, "utf8 content", Encoding.ASCII);
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<FtpTransferStatus> uploaded = await client.UploadFileAsync(source, "/" + ChineseName);
        CommResult<IReadOnlyList<FtpEntry>> listed = await client.ListAsync("/");
        CommResult<FtpTransferStatus> downloaded = await client.DownloadFileAsync("/" + ChineseName, local.File("copy.txt"));

        Assert.True(uploaded.IsSuccess, uploaded.ToString());
        Assert.True(listed.IsSuccess, listed.ToString());
        Assert.Contains(listed.Data!, e => e.Name == ChineseName);
        Assert.True(downloaded.IsSuccess, downloaded.ToString());
        Assert.Equal("utf8 content", File.ReadAllText(local.File("copy.txt"), Encoding.ASCII));
    }
}
