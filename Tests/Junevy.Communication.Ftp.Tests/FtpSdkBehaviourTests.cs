using FluentFTP;
using FluentFTP.Exceptions;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// FluentFTP 的行为钉住（P6-C14；第 18.2 节 F14–F18）：直接调用 SDK，断言它对"不存在"的各种情况的实际表现。
/// 适配层依赖这些行为（例如列空目录时是否需要本库再检查一次），SDK 升级改变行为时这些测试先失败。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpSdkBehaviourTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetListing_OnAMissingDirectory_ReturnsAnEmptyArray_WithoutThrowing(bool mlst)
    {
        await using FakeFtpServer server = FtpFixtures.Start(new FakeFtpOptions { AdvertiseMlst = mlst });
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        FtpListItem[] items = await sdk.GetListing("/missing", FtpListOption.Auto, CancellationToken.None);

        Assert.Empty(items);
        Assert.Contains(server.Commands.Snapshot(), c => c.Verb is "MLSD" or "LIST" && c.ReplyCode == 550);
    }

    [Fact]
    public async Task FileExists_OnAMissingFile_ReturnsFalse_WithoutThrowing()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        bool exists = await sdk.FileExists("/docs/none.txt", CancellationToken.None);

        Assert.False(exists);
    }

    [Fact]
    public async Task DirectoryExists_OnAMissingDirectory_ReturnsFalse_WithoutThrowing()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        bool exists = await sdk.DirectoryExists("/missing", CancellationToken.None);

        Assert.False(exists);
    }

    [Fact]
    public async Task GetFileSize_OnAMissingFile_ReturnsTheDefaultValue_WithoutThrowing()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        long size = await sdk.GetFileSize("/docs/none.txt", -1, CancellationToken.None);

        Assert.Equal(-1, size);
    }

    [Fact]
    public async Task GetFileSize_WithoutSizeSupport_ReturnsTheDefaultValue_AndSendsNoSizeCommand()
    {
        await using FakeFtpServer server = FtpFixtures.Start(new FakeFtpOptions { AdvertiseSize = false });
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        long size = await sdk.GetFileSize("/docs/recipe.txt", -1, CancellationToken.None);

        Assert.Equal(-1, size);
        Assert.DoesNotContain(server.Commands.Snapshot(), c => c.Verb == "SIZE");
    }

    [Fact]
    public async Task CreateDirectory_OnAnExistingDirectory_ReturnsFalse_WithoutThrowing()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        bool created = await sdk.CreateDirectory("/docs", true, CancellationToken.None);

        Assert.False(created);
    }

    [Fact]
    public async Task DeleteFile_OnAMissingFile_ThrowsFtpCommandException_WithCode550()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        FtpCommandException ex = await Assert.ThrowsAsync<FtpCommandException>(
            () => sdk.DeleteFile("/docs/none.txt", CancellationToken.None));

        Assert.Equal("550", ex.CompletionCode);
    }

    [Fact]
    public async Task Rename_OnAMissingSource_ThrowsFtpCommandException_WithCode550()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        FtpCommandException ex = await Assert.ThrowsAsync<FtpCommandException>(
            () => sdk.Rename("/missing", "/x", CancellationToken.None));

        Assert.Equal("550", ex.CompletionCode);
    }

    [Fact]
    public async Task DeleteDirectory_OnAMissingDirectory_ThrowsFtpCommandException_WithCode550()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using AsyncFtpClient sdk = await ConnectAsync(server);

        FtpCommandException ex = await Assert.ThrowsAsync<FtpCommandException>(
            () => sdk.DeleteDirectory("/missing", FtpListOption.Recursive, CancellationToken.None));

        Assert.Equal("550", ex.CompletionCode);
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
}
