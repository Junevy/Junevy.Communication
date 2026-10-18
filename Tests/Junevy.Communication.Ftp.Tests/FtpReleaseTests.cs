using System.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 释放的规则（P6-C8、P63-D4；计划 Task 6 第 1 条与第 1 节第 6 条）：释放时正在进行或排队的操作返回失败结果而不是抛异常；
/// 释放之后的操作返回 ConnectionClosed；释放之后没有未观察的任务异常（Q40）。涉及真实套接字，放入 <c>SocketTiming</c> 集合。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpReleaseTests
{
    [Fact]
    public async Task ReleaseWhileAnOperationIsInProgress_ReturnsAFailureResult_WithoutThrowing()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        FtpClient client = CreateClientWithSlowListing(server);
        Task<CommResult<IReadOnlyList<FtpEntry>>> inProgress = client.ListAsync("/docs");
        await Polling.WaitUntilAsync(() => server.Commands.Snapshot().Any(c => c.Verb == "MLSD"));

        var stopwatch = Stopwatch.StartNew();
        await client.DisposeAsync();
        CommResult<IReadOnlyList<FtpEntry>> result = await Arrival.WithinAsync(inProgress, 5000);
        stopwatch.Stop();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.ErrorKind, new[] { CommErrorKind.Cancelled, CommErrorKind.ConnectionClosed });
        // 释放取消了进行中的操作，不等待服务器的 2 秒延迟。
        Assert.True(stopwatch.ElapsedMilliseconds < 1500, $"release took {stopwatch.ElapsedMilliseconds} ms");
        CommResult<IReadOnlyList<FtpEntry>> later = await client.ListAsync("/docs");
        Assert.Equal(CommErrorKind.ConnectionClosed, later.ErrorKind);
    }

    [Fact]
    public async Task ReleaseWhileOperationsAreQueued_ReturnsAFailureResult_ForEachOne_AndDoesNotRunThem()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        FtpClient client = CreateClientWithSlowListing(server);
        Task<CommResult<IReadOnlyList<FtpEntry>>> first = client.ListAsync("/docs");
        await Polling.WaitUntilAsync(() => server.Commands.Snapshot().Any(c => c.Verb == "MLSD"));
        Task<CommResult<IReadOnlyList<FtpEntry>>> queuedList = client.ListAsync("/docs");
        Task<CommResult> queuedDelete = client.DeleteFileAsync("/docs/recipe.txt");

        await client.DisposeAsync();
        CommResult<IReadOnlyList<FtpEntry>> firstResult = await Arrival.WithinAsync(first, 5000);
        CommResult<IReadOnlyList<FtpEntry>> queuedResult = await Arrival.WithinAsync(queuedList, 5000);
        CommResult queuedRemoval = await Arrival.WithinAsync(queuedDelete, 5000);

        Assert.False(firstResult.IsSuccess);
        Assert.Equal(CommErrorKind.ConnectionClosed, queuedResult.ErrorKind);
        Assert.Equal(CommErrorKind.ConnectionClosed, queuedRemoval.ErrorKind);
        Assert.True(server.FileSystem.FileExists("/docs/recipe.txt"));
        Assert.DoesNotContain(server.Commands.Snapshot(), c => c.Verb == "DELE");
    }

    [Fact]
    public async Task OperationsAfterRelease_ReturnConnectionClosed_WithoutConnecting()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        await client.DisposeAsync();

        CommResult<IReadOnlyList<FtpEntry>> list = await client.ListAsync("/docs");
        CommResult<bool> exists = await client.FileExistsAsync("/docs/recipe.txt");
        CommResult removed = await client.DeleteFileAsync("/docs/recipe.txt");

        Assert.Equal(CommErrorKind.ConnectionClosed, list.ErrorKind);
        Assert.Equal(CommErrorKind.ConnectionClosed, exists.ErrorKind);
        Assert.Equal(CommErrorKind.ConnectionClosed, removed.ErrorKind);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task DownloadFileAfterRelease_IsConnectionClosed_AndLeavesTheLocalSideAlone()
    {
        // 释放之后不做任何本地检查或改动（P6.3 审阅 R6）：Skip 不返回 Skipped，Overwrite 不创建父目录。
        using var local = new TempDirectory();
        string existing = local.File("recipe.txt");
        File.WriteAllText(existing, "local");
        string nested = local.File(Path.Combine("nested", "recipe.txt"));
        await using FakeFtpServer server = FtpFixtures.Start();
        FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        await client.DisposeAsync();

        CommResult<FtpTransferStatus> skipped = await client.DownloadFileAsync("/docs/recipe.txt", existing, FtpExistsMode.Skip);
        CommResult<FtpTransferStatus> overwritten = await client.DownloadFileAsync("/docs/recipe.txt", nested);

        Assert.Equal(CommErrorKind.ConnectionClosed, skipped.ErrorKind);
        Assert.Equal(CommErrorKind.ConnectionClosed, overwritten.ErrorKind);
        Assert.False(Directory.Exists(local.File("nested")));
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task ReleaseAndGarbageCollection_LeaveNoUnobservedTaskExceptions()
    {
        int unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Interlocked.Increment(ref unobserved);
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await using FakeFtpServer server = FtpFixtures.Start();
            FtpClient client = CreateClientWithSlowListing(server);
            Task<CommResult<IReadOnlyList<FtpEntry>>> inProgress = client.ListAsync("/docs");
            await Polling.WaitUntilAsync(() => server.Commands.Snapshot().Any(c => c.Verb == "MLSD"));
            await client.DisposeAsync();
            await Arrival.WithinAsync(inProgress, 5000);
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.Equal(0, Volatile.Read(ref unobserved));
    }

    // MLSD 的应答延迟 2 秒，并放宽读超时：释放发生在操作进行中，而不是等超时之后。
    private static FtpClient CreateClientWithSlowListing(FakeFtpServer server)
    {
        server.Faults.DelayReply("MLSD", TimeSpan.FromSeconds(2));
        FtpClientOptions options = FtpTestClients.OptionsFor(server);
        options.ReadTimeout = 10000;
        return FtpClient.Create(options);
    }
}
