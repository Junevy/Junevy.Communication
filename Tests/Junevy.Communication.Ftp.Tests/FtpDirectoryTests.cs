using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 目录与文件操作（P63-D8；计划 Task 4 第 1 条）：逐级创建、递归删除、删除文件、重命名。
/// 不存在的对象由 SDK 以 550 抛出，结果带应答码。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpDirectoryTests
{
    [Fact]
    public async Task CreateDirectory_NestedPath_CreatesEveryLevel()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult result = await client.CreateDirectoryAsync("/a/b/c");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(server.FileSystem.DirectoryExists("/a"));
        Assert.True(server.FileSystem.DirectoryExists("/a/b"));
        Assert.True(server.FileSystem.DirectoryExists("/a/b/c"));
    }

    [Fact]
    public async Task CreateDirectory_WhenItAlreadyExists_Succeeds()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult result = await client.CreateDirectoryAsync("/docs");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(server.FileSystem.DirectoryExists("/docs"));
    }

    [Fact]
    public async Task CreateDirectory_WhenAFileIsAtThePath_IsRemoteError()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult result = await client.CreateDirectoryAsync("/docs/recipe.txt");

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.True(server.FileSystem.FileExists("/docs/recipe.txt"));
    }

    [Fact]
    public async Task DeleteDirectory_IsRecursive_RemovesFilesAndSubdirectories()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult result = await client.DeleteDirectoryAsync("/docs");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.False(server.FileSystem.DirectoryExists("/docs"));
        Assert.False(server.FileSystem.FileExists("/docs/recipe.txt"));
    }

    [Fact]
    public async Task DeleteDirectory_WhenMissing_IsRemoteError_WithTheReplyCode()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult result = await client.DeleteDirectoryAsync("/missing");

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(550, result.ProtocolErrorCode);
    }

    [Fact]
    public async Task DeleteFile_ForAnExistingFile_Succeeds()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult result = await client.DeleteFileAsync("/docs/recipe.txt");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.False(server.FileSystem.FileExists("/docs/recipe.txt"));
    }

    [Fact]
    public async Task DeleteFile_ForAMissingFile_IsRemoteError_WithTheReplyCode()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult result = await client.DeleteFileAsync("/docs/none.txt");

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(550, result.ProtocolErrorCode);
    }

    [Fact]
    public async Task Rename_ForAnExistingSource_Succeeds()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult result = await client.RenameAsync("/docs/recipe.txt", "/docs/soup.txt");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.False(server.FileSystem.FileExists("/docs/recipe.txt"));
        Assert.True(server.FileSystem.FileExists("/docs/soup.txt"));
    }

    [Fact]
    public async Task Rename_MissingSource_IsRemoteError_WithTheReplyCode()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult result = await client.RenameAsync("/docs/none.txt", "/docs/soup.txt");

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(550, result.ProtocolErrorCode);
    }
}
