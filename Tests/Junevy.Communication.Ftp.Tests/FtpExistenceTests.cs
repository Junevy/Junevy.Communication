using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>存在性与大小（P63-D10；计划 Task 4 第 1 条）。</summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpExistenceTests
{
    [Fact]
    public async Task FileExists_ForAnExistingFile_IsTrue()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<bool> result = await client.FileExistsAsync("/docs/recipe.txt");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(result.Data);
    }

    [Fact]
    public async Task FileExists_ForAMissingFile_IsFalse_NotAnError()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<bool> result = await client.FileExistsAsync("/docs/none.txt");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.False(result.Data);
    }

    [Fact]
    public async Task DirectoryExists_ForAnExistingDirectory_IsTrue()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<bool> result = await client.DirectoryExistsAsync("/docs");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(result.Data);
    }

    [Fact]
    public async Task DirectoryExists_ForAMissingDirectory_IsFalse_NotAnError()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<bool> result = await client.DirectoryExistsAsync("/missing");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.False(result.Data);
    }

    [Fact]
    public async Task GetFileSize_ForAnExistingFile_ReturnsTheSize()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<long> result = await client.GetFileSizeAsync("/docs/recipe.txt");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(12L, result.Data);
    }

    [Fact]
    public async Task GetFileSize_ForAMissingFile_IsRemoteError_WithoutASentinelValue()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<long> result = await client.GetFileSizeAsync("/docs/none.txt");

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
    }

    [Fact]
    public async Task GetFileSize_WhenTheServerHasNoSize_IsRemoteError_AndSendsNoSizeCommand()
    {
        await using FakeFtpServer server = FtpFixtures.Start(new FakeFtpOptions { AdvertiseSize = false });
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<long> result = await client.GetFileSizeAsync("/docs/recipe.txt");

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        // SDK 在 SIZE 不可用时直接返回默认值，不发送命令（第 18.2 节 F16）。
        Assert.DoesNotContain(server.Commands.Snapshot(), c => c.Verb == "SIZE");
    }
}
