using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 列目录（P63-D3、D9；计划 Task 4 第 1 条）：按需连接；MLSD 与 LIST 两种模式；空目录；不存在的目录。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpListingTests
{
    [Fact]
    public async Task OnDemandConnect_ListAsync_ConnectsWithoutConnectAsync()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<IReadOnlyList<FtpEntry>> result = await client.ListAsync("/docs");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(client.IsConnected);
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task Mlsd_ReturnsNamesTypesSizesAndExactTimes_WithoutDotEntries()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<IReadOnlyList<FtpEntry>> result = await client.ListAsync("/docs");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Contains(server.Commands.Snapshot(), c => c.Verb == "MLSD");
        FtpEntry[] entries = result.Data!.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, entries.Length);

        Assert.Equal("archive", entries[0].Name);
        Assert.Equal("/docs/archive", entries[0].FullName);
        Assert.Equal(FtpEntryType.Directory, entries[0].Type);
        Assert.Null(entries[0].Size);

        Assert.Equal("recipe.txt", entries[1].Name);
        Assert.Equal("/docs/recipe.txt", entries[1].FullName);
        Assert.Equal(FtpEntryType.File, entries[1].Type);
        Assert.Equal(12L, entries[1].Size);
        Assert.Equal(FtpFixtures.RecipeModified, entries[1].Modified);
    }

    [Fact]
    public async Task ListMode_ReturnsTheSameEntries_WhenTheServerHasNoMlst()
    {
        await using FakeFtpServer server = FtpFixtures.Start(new FakeFtpOptions { AdvertiseMlst = false });
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<IReadOnlyList<FtpEntry>> result = await client.ListAsync("/docs");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Contains(server.Commands.Snapshot(), c => c.Verb == "LIST");
        Assert.DoesNotContain(server.Commands.Snapshot(), c => c.Verb == "MLSD");
        FtpEntry[] entries = result.Data!.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(FtpEntryType.Directory, entries[0].Type);
        Assert.Null(entries[0].Size);
        Assert.Equal(FtpEntryType.File, entries[1].Type);
        Assert.Equal(12L, entries[1].Size);
        // LIST 只给出日期（Unix 风格的 "MMM dd  yyyy"），时间不精确，这里只比较日期。
        Assert.Equal(FtpFixtures.RecipeModified.Date, entries[1].Modified!.Value.Date);
    }

    [Fact]
    public async Task EmptyDirectory_SucceedsWithAnEmptyList()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<IReadOnlyList<FtpEntry>> result = await client.ListAsync("/empty");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Empty(result.Data!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingDirectory_IsRemoteError_InBothListingModes(bool mlsd)
    {
        await using FakeFtpServer server = FtpFixtures.Start(new FakeFtpOptions { AdvertiseMlst = mlsd });
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<IReadOnlyList<FtpEntry>> result = await client.ListAsync("/missing");

        // SDK 对不存在的目录静默返回空列表（第 18.2 节 F14），本库再检查一次目录，因此没有应答码。
        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Null(result.ProtocolErrorCode);
    }

    [Fact]
    public async Task RelativePath_IsResolvedAgainstTheLoginDirectory()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<IReadOnlyList<FtpEntry>> result = await client.ListAsync("docs");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(2, result.Data!.Count);
    }
}
