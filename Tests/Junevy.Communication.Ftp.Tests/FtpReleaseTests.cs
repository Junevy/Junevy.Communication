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
        server.Faults.DelayReply("MLSD", TimeSpan.FromSeconds(2));
        FtpClientOptions options = FtpTestClients.OptionsFor(server);
        options.ReadTimeout = 10000;
        FtpClient client = FtpClient.Create(options);
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
        server.Faults.DelayReply("MLSD", TimeSpan.FromSeconds(2));
        FtpClientOptions options = FtpTestClients.OptionsFor(server);
        options.ReadTimeout = 10000;
        FtpClient client = FtpClient.Create(options);
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
            server.Faults.DelayReply("MLSD", TimeSpan.FromSeconds(2));
            FtpClientOptions options = FtpTestClients.OptionsFor(server);
            options.ReadTimeout = 10000;
            FtpClient client = FtpClient.Create(options);
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
}
