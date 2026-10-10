using System.Diagnostics;
using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using static Junevy.Communication.Channels.Tests.RouterTestHelpers;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 连接监督器测试（计划 9.4 的 <c>SupervisorTests</c>）。使用 <see cref="FakeConnectionDriver"/>，全部在内存中完成。
/// 时间断言使用区间（计划第 1 节第 5 条）；"不应发生"的断言在合理等待之后检查。
/// </summary>
public sealed class SupervisorTests
{
    [Fact(Timeout = 20000)]
    public async Task Connect_Success_TransitionsAndRaisesInOrder()
    {
        var driver = new FakeConnectionDriver();
        await using var supervisor = NewSupervisor(driver);
        var states = new StateRecorder();
        states.Attach(supervisor);

        CommResult result = await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);

        Assert.True(result.IsSuccess, result.ToString());
        await WaitForEntriesAsync(states, 2);
        Assert.Equal(
            new List<(ConnectionState, ConnectionState)>
            {
                (ConnectionState.Disconnected, ConnectionState.Connecting),
                (ConnectionState.Connecting, ConnectionState.Connected),
            },
            states.Transitions());
        Assert.Equal(ConnectionState.Connected, supervisor.State);
        Assert.Equal(1, supervisor.Generation);
        Assert.Equal(1, driver.OpenCalls);
    }

    [Fact(Timeout = 20000)]
    public async Task Connect_Failure_NoReconnect_ReturnsToDisconnected()
    {
        var driver = new FakeConnectionDriver((attempt, token) => FakeConnectionDriver.Refuse());
        await using var supervisor = NewSupervisor(driver);
        var states = new StateRecorder();
        states.Attach(supervisor);

        CommResult result = await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        await WaitForEntriesAsync(states, 2);
        Assert.Equal(
            new List<(ConnectionState, ConnectionState)>
            {
                (ConnectionState.Disconnected, ConnectionState.Connecting),
                (ConnectionState.Connecting, ConnectionState.Disconnected),
            },
            states.Transitions());
        Assert.Equal(ConnectionState.Disconnected, supervisor.State);
        Assert.Equal(0, supervisor.Generation);
        Assert.Equal(1, driver.OpenCalls);
    }

    [Fact(Timeout = 20000)]
    public async Task Connect_Failure_OnInitialFailure_StartsReconnect()
    {
        var driver = new FakeConnectionDriver((attempt, token) => attempt == 3 ? FakeConnectionDriver.Succeed() : FakeConnectionDriver.Refuse());
        var statistics = new ConnectionStatistics();
        await using var supervisor = NewSupervisor(driver, new FixedIntervalBackoff(20, 0), reconnectOnInitialFailure: true, statistics);
        var states = new StateRecorder();
        states.Attach(supervisor);

        CommResult first = await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);

        Assert.False(first.IsSuccess);
        Assert.True(await WithinAsync(supervisor.WaitForConnectedAsync(3000, CancellationToken.None), 4000));
        await WaitForEntriesAsync(states, 3);
        Assert.Equal(
            new List<(ConnectionState, ConnectionState)>
            {
                (ConnectionState.Disconnected, ConnectionState.Connecting),
                (ConnectionState.Connecting, ConnectionState.Reconnecting),
                (ConnectionState.Reconnecting, ConnectionState.Connected),
            },
            states.Transitions());
        Assert.Equal(3, driver.OpenCalls);
        Assert.Equal(1, statistics.ReconnectCount);
    }

    [Fact(Timeout = 20000)]
    public async Task Connect_UserCancel_Throws_AndDisconnected()
    {
        var driver = new FakeConnectionDriver((attempt, token) => NeverCompletes(token));
        await using var supervisor = NewSupervisor(driver);
        var states = new StateRecorder();
        states.Attach(supervisor);

        using var cancellation = new CancellationTokenSource();
        Task<CommResult> connecting = supervisor.ConnectAsync(cancellation.Token);
        await WaitUntilAsync(() => driver.OpenCalls == 1, 2000);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.Equal(ConnectionState.Disconnected, supervisor.State);
        await WaitForEntriesAsync(states, 2);
        Assert.Equal(ConnectionState.Disconnected, states.Last!.Current);
    }

    [Fact(Timeout = 20000)]
    public async Task Lost_WithReconnect_ReconnectsWithInterval()
    {
        // 第 1 次为初始连接；随后的两次重连失败，第 4 次成功。固定间隔 200 ms：约 600 ms 后重新连上。
        var driver = new FakeConnectionDriver((attempt, token) => attempt == 1 || attempt == 4
            ? FakeConnectionDriver.Succeed()
            : FakeConnectionDriver.Refuse());
        await using var supervisor = NewSupervisor(driver, new FixedIntervalBackoff(200, 0));
        var states = new StateRecorder();
        states.Attach(supervisor);
        await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);
        await WaitForEntriesAsync(states, 2);

        long lostAt = Stopwatch.GetTimestamp();
        supervisor.ReportConnectionLost(1, DisconnectReason.RemoteClosed, null);

        await WaitForEntriesAsync(states, 4);
        double elapsedMs = (states.Entries[3].Timestamp - lostAt) * 1000.0 / Stopwatch.Frequency;
        Assert.Equal(ConnectionState.Connected, states.Entries[3].Current);
        Assert.InRange(elapsedMs, 480d, 2600d);
        Assert.Equal(4, driver.OpenCalls);
        Assert.Equal(1, driver.CloseCalls);
    }

    [Fact(Timeout = 20000)]
    public async Task Lost_StaleGeneration_Ignored()
    {
        var driver = new FakeConnectionDriver();
        await using var supervisor = NewSupervisor(driver);
        var states = new StateRecorder();
        states.Attach(supervisor);
        await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);

        supervisor.ReportConnectionLost(0, DisconnectReason.RemoteClosed, null);
        supervisor.ReportConnectionLost(99, DisconnectReason.RemoteClosed, null);

        await Task.Delay(300);
        Assert.Equal(ConnectionState.Connected, supervisor.State);
        Assert.Equal(0, driver.CloseCalls);
        Assert.Equal(2, states.Entries.Count);
    }

    [Fact(Timeout = 20000)]
    public async Task Lost_Twice_HandledOnce()
    {
        var driver = new FakeConnectionDriver();
        await using var supervisor = NewSupervisor(driver);
        await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);

        // 同一代次并发报告 10 次：只能关闭一次。
        var reports = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => supervisor.ReportConnectionLost(1, DisconnectReason.RemoteClosed, null)))
            .ToArray();
        await WithinAsync(Task.WhenAll(reports), 3000);

        await WaitUntilAsync(() => supervisor.State == ConnectionState.Disconnected, 3000);
        await Task.Delay(200);
        Assert.Equal(1, driver.CloseCalls);
        Assert.Equal(new[] { DisconnectReason.RemoteClosed }, driver.CloseReasons);
    }

    [Fact(Timeout = 20000)]
    public async Task Reconnect_Exhausted_Disconnected()
    {
        var driver = new FakeConnectionDriver((attempt, token) => attempt == 1 ? FakeConnectionDriver.Succeed() : FakeConnectionDriver.Refuse());
        await using var supervisor = NewSupervisor(driver, new FixedIntervalBackoff(10, 2));
        var states = new StateRecorder();
        states.Attach(supervisor);
        await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);

        supervisor.ReportConnectionLost(1, DisconnectReason.RemoteClosed, null);

        await WaitUntilAsync(() => states.Last?.Reason == DisconnectReason.ReconnectExhausted, 3000);
        StateEntry last = states.Last!;
        Assert.Equal(ConnectionState.Disconnected, last.Current);
        Assert.Equal(2, last.ReconnectAttempt);
        Assert.Equal(ConnectionState.Disconnected, supervisor.State);
        Assert.Equal(3, driver.OpenCalls);
    }

    [Fact(Timeout = 20000)]
    public async Task Disconnect_DuringReconnect_StopsLoop()
    {
        var driver = new FakeConnectionDriver((attempt, token) => attempt == 1 ? FakeConnectionDriver.Succeed() : FakeConnectionDriver.Refuse());
        await using var supervisor = NewSupervisor(driver, new FixedIntervalBackoff(50, 0));
        await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);
        supervisor.ReportConnectionLost(1, DisconnectReason.RemoteClosed, null);
        await WaitUntilAsync(() => driver.OpenCalls >= 3, 3000);

        await WithinAsync(supervisor.DisconnectAsync(0, CancellationToken.None), 3000);

        Assert.Equal(ConnectionState.Disconnected, supervisor.State);
        int opensAfterDisconnect = driver.OpenCalls;
        await Task.Delay(1000);
        Assert.Equal(opensAfterDisconnect, driver.OpenCalls);
    }

    [Fact(Timeout = 20000)]
    public async Task Disconnect_ThenNoAutoReconnect()
    {
        var driver = new FakeConnectionDriver();
        await using var supervisor = NewSupervisor(driver, new FixedIntervalBackoff(10, 0));
        await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);
        await WithinAsync(supervisor.DisconnectAsync(0, CancellationToken.None), 2000);

        // 断开之后，旧代次的丢失报告不能触发重连。
        supervisor.ReportConnectionLost(1, DisconnectReason.RemoteClosed, null);

        await Task.Delay(300);
        Assert.Equal(ConnectionState.Disconnected, supervisor.State);
        Assert.Equal(1, driver.OpenCalls);
        Assert.Equal(1, driver.CloseCalls);
    }

    [Fact(Timeout = 20000)]
    public async Task Connect_DuringReconnect_SupersedesLoop()
    {
        // 重连间隔 60 秒：若没有被取代，循环会一直等待。ConnectAsync 必须立即取消循环并打开连接。
        var driver = new FakeConnectionDriver((attempt, token) => FakeConnectionDriver.Succeed());
        await using var supervisor = NewSupervisor(driver, new FixedIntervalBackoff(60_000, 0));
        await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);
        supervisor.ReportConnectionLost(1, DisconnectReason.RemoteClosed, null);
        await WaitUntilAsync(() => supervisor.State == ConnectionState.Reconnecting, 2000);

        CommResult result = await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(ConnectionState.Connected, supervisor.State);
        Assert.Equal(2, supervisor.Generation);
        Assert.Equal(2, driver.OpenCalls);
        await Task.Delay(300);
        Assert.Equal(2, driver.OpenCalls);
    }

    [Fact(Timeout = 20000)]
    public async Task StateChangedHandler_CallsDisconnect_NoDeadlock()
    {
        var driver = new FakeConnectionDriver();
        await using var supervisor = NewSupervisor(driver);
        var disconnectReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.StateChanged += (sender, args) =>
        {
            if (args.CurrentState != ConnectionState.Connected)
                return;

            // 同步阻塞等待：派发任务在处理器内等待 DisconnectAsync 完成，DisconnectAsync 不能等待派发任务。
            DisconnectOnDispatchThread(supervisor);
            disconnectReturned.TrySetResult(true);
        };

        await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);

        Task finished = await Task.WhenAny(disconnectReturned.Task, Task.Delay(2000));
        Assert.Same(disconnectReturned.Task, finished);
        await WaitUntilAsync(() => supervisor.State == ConnectionState.Disconnected, 2000);
    }

    [Fact(Timeout = 20000)]
    public async Task WaitForConnected_TrueAndFalse()
    {
        var driver = new FakeConnectionDriver();
        await using var supervisor = NewSupervisor(driver);

        Assert.False(await WithinAsync(supervisor.WaitForConnectedAsync(50, CancellationToken.None), 2000));

        Task<bool> waiting = supervisor.WaitForConnectedAsync(3000, CancellationToken.None);
        await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);
        Assert.True(await WithinAsync(waiting, 3000));

        Assert.True(await WithinAsync(supervisor.WaitForConnectedAsync(0, CancellationToken.None), 2000));
    }

    [Fact(Timeout = 20000)]
    public async Task ConcurrentConnectDisconnectDispose_NoExceptions()
    {
        // 50 轮：每轮并发地发起连接、断开、报告丢失，同时释放。只允许 ObjectDisposedException（释放之后的调用）。
        for (int round = 0; round < 50; round++)
        {
            var driver = new FakeConnectionDriver(async (attempt, token) =>
            {
                await Task.Delay(attempt % 3, token);
                return attempt % 4 == 0 ? CommResult.Fail("Scripted failure.", CommErrorKind.ConnectionClosed) : CommResult.Success();
            });
            var supervisor = NewSupervisor(driver, new FixedIntervalBackoff(1, 0), reconnectOnInitialFailure: true);
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            var operations = new List<Task>();
            for (int i = 0; i < 8; i++)
            {
                int kind = (round + i) % 4;
                operations.Add(Task.Run(async () =>
                {
                    try
                    {
                        switch (kind)
                        {
                            case 0:
                                await supervisor.ConnectAsync(CancellationToken.None);
                                break;

                            case 1:
                                await supervisor.DisconnectAsync(0, CancellationToken.None);
                                break;

                            case 2:
                                supervisor.ReportConnectionLost(supervisor.Generation, DisconnectReason.RemoteClosed, null);
                                break;

                            default:
                                await supervisor.WaitForConnectedAsync(5, CancellationToken.None);
                                break;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // 释放之后的调用：预期行为。
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                    }
                }));
            }

            Task disposing = Task.Run(() => supervisor.DisposeAsync().AsTask());
            operations.Add(disposing);

            Task all = Task.WhenAll(operations);
            Assert.Same(all, await Task.WhenAny(all, Task.Delay(10000)));
            Assert.Empty(errors);
            Assert.Equal(ConnectionState.Disposed, supervisor.State);
        }
    }

    [Fact(Timeout = 20000)]
    public async Task Lost_DuringConnecting_IsHandledAfterConnect()
    {
        // 故障在驱动执行 OpenAsync 期间报告（连接还未进入 Connected）：必须记住它，连接建立后立即按丢失处理。
        ConnectionSupervisor? supervisor = null;
        var driver = new FakeConnectionDriver((attempt, token) =>
        {
            supervisor!.ReportConnectionLost(supervisor.OpeningGeneration, DisconnectReason.RemoteClosed, null);
            return FakeConnectionDriver.Succeed();
        });
        await using (ConnectionSupervisor owned = NewSupervisor(driver))
        {
            supervisor = owned;
            var states = new StateRecorder();
            states.Attach(owned);

            CommResult result = await WithinAsync(owned.ConnectAsync(CancellationToken.None), 2000);

            Assert.True(result.IsSuccess, result.ToString());
            await WaitUntilAsync(() => owned.State == ConnectionState.Disconnected, 3000);
            await WaitForEntriesAsync(states, 3);
            Assert.Equal(DisconnectReason.RemoteClosed, states.Last!.Reason);
            Assert.Equal(1, driver.CloseCalls);
        }
    }

    [Fact(Timeout = 20000)]
    public async Task Disconnect_AfterQueuedConnectFailed_StopsReconnectItStarted()
    {
        // 第 1 次打开阻塞在一个门上（持有生命周期锁）；随后排队的 ConnectAsync 与 DisconnectAsync 按顺序获得锁。
        // 排队的 ConnectAsync 清除了断开标记并在打开失败后启动重连循环：DisconnectAsync 必须停止这个循环。
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new FakeConnectionDriver(async (attempt, token) =>
        {
            if (attempt == 1)
                await gate.Task;

            return CommResult.Fail("The scripted open failed.", CommErrorKind.ConnectionClosed);
        });
        await using var supervisor = NewSupervisor(driver, new FixedIntervalBackoff(20, 0), reconnectOnInitialFailure: true);

        Task<CommResult> firstConnect = supervisor.ConnectAsync(CancellationToken.None);
        await WaitUntilAsync(() => driver.OpenCalls == 1, 2000);
        Task<CommResult> queuedConnect = supervisor.ConnectAsync(CancellationToken.None);
        Task disconnect = supervisor.DisconnectAsync(0, CancellationToken.None);

        gate.SetResult(true);
        await WithinAsync(firstConnect, 3000);
        await WithinAsync(queuedConnect, 3000);
        await WithinAsync(disconnect, 3000);

        Assert.Equal(ConnectionState.Disconnected, supervisor.State);
        int opensAfterDisconnect = driver.OpenCalls;
        await Task.Delay(500);
        Assert.Equal(opensAfterDisconnect, driver.OpenCalls);
    }

    [Fact(Timeout = 20000)]
    public async Task DisposeAsync_IsIdempotentAndDisposesToTerminalState()
    {
        var driver = new FakeConnectionDriver();
        var supervisor = NewSupervisor(driver);
        await WithinAsync(supervisor.ConnectAsync(CancellationToken.None), 2000);

        await WithinAsync(supervisor.DisposeAsync().AsTask(), 3000);
        await WithinAsync(supervisor.DisposeAsync().AsTask(), 3000);

        Assert.Equal(ConnectionState.Disposed, supervisor.State);
        Assert.Equal(1, driver.CloseCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => supervisor.ConnectAsync(CancellationToken.None));
    }

    private static ConnectionSupervisor NewSupervisor(FakeConnectionDriver driver, IBackoffPolicy? reconnectPolicy = null,
                                                      bool reconnectOnInitialFailure = false, ConnectionStatistics? statistics = null)
    {
        var supervisor = new ConnectionSupervisor("supervisor-test", driver, reconnectPolicy, reconnectOnInitialFailure,
                                                  statistics ?? new ConnectionStatistics(), new TestLogger());
        return supervisor;
    }

    // 在状态事件处理器的派发线程上同步等待断开：这正是要验证的重入场景，因此放在测试方法之外。
    private static void DisconnectOnDispatchThread(ConnectionSupervisor supervisor)
        => supervisor.DisconnectAsync(0, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task WaitForEntriesAsync(StateRecorder states, int count)
        => await WaitUntilAsync(() => states.Entries.Count >= count, 3000);

    private static async Task<CommResult> NeverCompletes(CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        return CommResult.Success();
    }
}
