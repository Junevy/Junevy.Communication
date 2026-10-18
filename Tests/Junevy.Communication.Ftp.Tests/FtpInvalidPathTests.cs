using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 非法路径（P63-D5；计划 Task 4 第 1 条）：空路径返回 InvalidRequest 且不连接服务器；null 抛 ArgumentNullException。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpInvalidPathTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyPath_IsInvalidRequest_AndNoConnectionIsMade(string path)
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<IReadOnlyList<FtpEntry>> list = await client.ListAsync(path);
        CommResult<bool> exists = await client.FileExistsAsync(path);
        CommResult remove = await client.DeleteFileAsync(path);

        Assert.Equal(CommErrorKind.InvalidRequest, list.ErrorKind);
        Assert.Equal(CommErrorKind.InvalidRequest, exists.ErrorKind);
        Assert.Equal(CommErrorKind.InvalidRequest, remove.ErrorKind);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task NullPath_ThrowsArgumentNullException()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.ListAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.RenameAsync(null!, "/x"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.RenameAsync("/docs/recipe.txt", null!));
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task NullArgument_ThrowsBeforeTheOtherArgumentsAreChecked()
    {
        // null 是编程错误，先于其他参数的取值检查（P6.3 审阅 R5）：另一个参数非法时同样抛出，而不是返回 InvalidRequest。
        await using FtpClient client = FtpClient.Create(FtpTestClients.ValidOptions());
        using var source = new MemoryStream();

        ArgumentNullException upload = await Assert.ThrowsAsync<ArgumentNullException>(() => client.UploadFileAsync("missing.txt", null!));
        ArgumentNullException stream = await Assert.ThrowsAsync<ArgumentNullException>(() => client.UploadAsync(source, null!, (FtpExistsMode)99));
        ArgumentNullException download = await Assert.ThrowsAsync<ArgumentNullException>(() => client.DownloadFileAsync("", null!));
        ArgumentNullException rename = await Assert.ThrowsAsync<ArgumentNullException>(() => client.RenameAsync(null!, ""));

        Assert.Equal("remotePath", upload.ParamName);
        Assert.Equal("remotePath", stream.ParamName);
        Assert.Equal("localPath", download.ParamName);
        Assert.Equal("remotePath", rename.ParamName);
    }
}
