using System.Diagnostics;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using static Junevy.Communication.Serial.Tests.SerialTestHelpers;

namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 串口通道的行为测试（计划 14.3）。全部使用假端口（<see cref="FakeSerialPortHandle"/>），不打开任何真实串口。
/// 含时间相关断言，因此与其它时序测试同属 <c>SocketTiming</c> 集合，同集合内串行执行。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class SerialChannelTests
{
    [Fact(Timeout = 30000)]
    public async Task Open_ConnectsAndDiscardsInBuffer()
    {
        var factory = new FakeSerialPortHandleFactory();
        await using var channel = CreateChannel(factory, CreateConfig());

        CommResult result = await WithinAsync(channel.ConnectAsync(), 5000);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(ConnectionState.Connected, channel.State);
        FakeSerialPortHandle handle = Assert.Single(factory.Handles);
        Assert.Equal(1, handle.DiscardCount);
        Assert.True(handle.DiscardedWhileOpen, "DiscardInBuffer must run after the port has been opened.");
    }

    [Fact(Timeout = 30000)]
    public async Task Open_Throws_ReturnsConnectionClosed()
    {
        var factory = new FakeSerialPortHandleFactory
        {
            Configure = (index, handle) => handle.OpenException = new UnauthorizedAccessException("Access to the port 'COM3' is denied."),
        };
        await using var channel = CreateChannel(factory, CreateConfig());

        CommResult result = await WithinAsync(channel.ConnectAsync(), 5000);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Contains("COM3", result.ErrorMessage!);
        Assert.Contains("Access to the port 'COM3' is denied.", result.ErrorMessage!);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
        Assert.Equal(1, Assert.Single(factory.Handles).DisposeCount);
    }

    [Fact(Timeout = 30000)]
    public async Task Open_Hangs_ReturnsTimeout()
    {
        // Open 阻塞，直到测试释放门闩（计划中的"阻塞 5 秒"改为门闩，断言相同，测试更快）。
        using var gate = new ManualResetEventSlim(false);
        var factory = new FakeSerialPortHandleFactory { Configure = (index, handle) => handle.OpenGate = gate };
        await using var channel = CreateChannel(factory, CreateConfig(openTimeout: 300));

        var stopwatch = Stopwatch.StartNew();
        CommResult result = await WithinAsync(channel.ConnectAsync(), 5000);
        stopwatch.Stop();

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 240d, 2300d);
        Assert.Equal(ConnectionState.Disconnected, channel.State);

        // Open 最终完成时驱动释放端口（计划 14.2）：此时端口已没有所有者。
        FakeSerialPortHandle handle = Assert.Single(factory.Handles);
        gate.Set();
        await WaitUntilAsync(() => handle.DisposeCount == 1, 3000);
    }

    [Fact(Timeout = 30000)]
    public async Task Open_UserCancelled_ThrowsAndReleasesPort()
    {
        // 驱动仍在打开时用户取消：ConnectAsync 抛出 OperationCanceledException，之后驱动返回的端口仍被释放。
        using var gate = new ManualResetEventSlim(false);
        var factory = new FakeSerialPortHandleFactory { Configure = (index, handle) => handle.OpenGate = gate };
        await using var channel = CreateChannel(factory, CreateConfig(openTimeout: 10000));
        using var cancel = new CancellationTokenSource();

        Task<CommResult> connecting = channel.ConnectAsync(cancel.Token);
        await WaitUntilAsync(() => factory.CreateCount == 1, 3000);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WithinAsync(connecting, 3000));
        Assert.Equal(ConnectionState.Disconnected, channel.State);

        FakeSerialPortHandle handle = Assert.Single(factory.Handles);
        gate.Set();
        await WaitUntilAsync(() => handle.DisposeCount == 1, 3000);
    }

    [Fact(Timeout = 30000)]
    public async Task RequestTimeout_DoesNotReopenPort()
    {
        // 串口的请求超时不重新打开端口（ResetOnRequestTimeout 固定为 false，迟到应答由 LateReplyWindow 处理，设计文档第 10 节）。
        var factory = new FakeSerialPortHandleFactory();
        await using var channel = CreateChannel(factory, CreateConfig(framing: DelimiterFraming(), requestTimeout: 300));
        Assert.True((await WithinAsync(channel.ConnectAsync(), 5000)).IsSuccess);

        // 设备不回复。
        CommResult<byte[]> reply = await WithinAsync(channel.RequestAsync(Ascii("REQ")), 5000);

        Assert.Equal(CommErrorKind.Timeout, reply.ErrorKind);
        Assert.Equal(ConnectionState.Connected, channel.State);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(0, channel.Statistics.ReconnectCount);
    }

    [Fact(Timeout = 30000)]
    public async Task IdleGap_DefaultFraming_EndToEnd()
    {
        var factory = new FakeSerialPortHandleFactory();
        await using var channel = CreateChannel(factory, CreateConfig());
        var sink = new FrameSink(channel);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 5000)).IsSuccess);
        FakeSerialPortHandle handle = Assert.Single(factory.Handles);

        // "AB" 与 "CD" 之间没有静默（远小于 20 ms），合并为一帧；之后超过 100 ms 的静默分隔出两帧。
        await WriteRawAsync(handle.Device, Ascii("AB"));
        await WriteRawAsync(handle.Device, Ascii("CD"));
        await WaitUntilAsync(() => sink.Count >= 1, 3000);
        await Task.Delay(100);
        await WriteRawAsync(handle.Device, Ascii("EF"));
        await WaitUntilAsync(() => sink.Count >= 2, 3000);
        await Task.Delay(100);
        await WriteRawAsync(handle.Device, Ascii("GH"));
        await WaitUntilAsync(() => sink.Count >= 3, 3000);

        IReadOnlyList<byte[]> frames = sink.Snapshot();
        Assert.Equal(3, frames.Count);
        Assert.Equal(Ascii("ABCD"), frames[0]);
        Assert.Equal(Ascii("EF"), frames[1]);
        Assert.Equal(Ascii("GH"), frames[2]);
        Assert.Equal(3, channel.Statistics.FramesReceived);
    }

    [Fact(Timeout = 30000)]
    public async Task PartialFrame_Discard_Continues()
    {
        // 长度字段占 1 字节，交付时去掉它；半帧超过 PartialFrameTimeout 后残余字节被丢弃（PartialFrameAction.Discard），连接保持。
        var framing = new FramingOptions { Mode = FramingMode.LengthField, LengthFieldOffset = 0, LengthFieldSize = 1, InitialBytesToStrip = 1 };
        var factory = new FakeSerialPortHandleFactory();
        await using var channel = CreateChannel(factory, CreateConfig(framing: framing, partialFrameTimeout: 200));
        var sink = new FrameSink(channel);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 5000)).IsSuccess);
        FakeSerialPortHandle handle = Assert.Single(factory.Handles);

        // 声明 5 字节负载，只到达 2 字节。
        await WriteRawAsync(handle.Device, new byte[] { 0x05, (byte)'A', (byte)'B' });
        await WaitUntilAsync(() => channel.Statistics.ProtocolErrors >= 1, 5000);

        await WriteRawAsync(handle.Device, new byte[] { 0x02, (byte)'O', (byte)'K' });
        await WaitUntilAsync(() => sink.Count >= 1, 3000);

        Assert.Equal(ConnectionState.Connected, channel.State);
        IReadOnlyList<byte[]> frames = sink.Snapshot();
        Assert.Single(frames);
        Assert.Equal(Ascii("OK"), frames[0]);
    }

    [Fact(Timeout = 30000)]
    public async Task ReadIOException_ReconnectsByReopening()
    {
        var factory = new FakeSerialPortHandleFactory();
        await using var channel = CreateChannel(factory, CreateConfig(framing: DelimiterFraming(), reconnect: true));
        var states = new List<ConnectionStateChangedEventArgs>();
        channel.StateChanged += (sender, args) =>
        {
            lock (states)
                states.Add(args);
        };
        Assert.True((await WithinAsync(channel.ConnectAsync(), 5000)).IsSuccess);

        // 链路中断：读取抛出 IOException，驱动以 RemoteClosed 报告，监督器按重连策略重新打开端口。
        factory.Handles[0].Pair.Abort();

        await WaitUntilAsync(() => factory.CreateCount == 2 && channel.State == ConnectionState.Connected, 5000);
        await WaitUntilAsync(() => CountConnected(states) == 2, 3000);
        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(1, channel.Statistics.ReconnectCount);

        ConnectionStateChangedEventArgs reconnecting;
        lock (states)
            reconnecting = Assert.Single(states, args => args.CurrentState == ConnectionState.Reconnecting);
        Assert.Equal(DisconnectReason.RemoteClosed, reconnecting.Reason);

        // 重新打开后的端口照常收发。
        var sink = new FrameSink(channel);
        await WriteRawAsync(factory.Handles[1].Device, Ascii("HELLO\r\n"));
        await WaitUntilAsync(() => sink.Count >= 1, 3000);
        Assert.Equal(Ascii("HELLO"), Assert.Single(sink.Snapshot()));
    }

    [Fact(Timeout = 30000)]
    public async Task SharedChannel_ViaAlias_SequentialAcrossLogicalDevices()
    {
        // 两个逻辑设备（站号 1 与 2）共用一条串口：别名指向同一个通道，请求必须串行，各自拿到对应的应答。
        var factory = new FakeSerialPortHandleFactory();
        using ChannelFactory channels = ChannelFactoryBuilder.Create()
            .WithCreator(new FakeSerialChannelCreator(factory))
            .Build();
        channels.GetOrAdd("COM3", CreateConfig(framing: DelimiterFraming()));
        Assert.True(channels.RegisterAlias("slave-1", "COM3"));
        Assert.True(channels.RegisterAlias("slave-2", "COM3"));

        ISerialChannel slave1 = channels.GetRequired<ISerialChannel>("slave-1");
        ISerialChannel slave2 = channels.GetRequired<ISerialChannel>("slave-2");
        Assert.Same(slave1, slave2);
        Assert.True((await WithinAsync(slave1.ConnectAsync(), 5000)).IsSuccess);

        using var cancel = new CancellationTokenSource();
        var device = new SerialDevice(Assert.Single(factory.Handles).Device, DelimiterCodec(),
                                      request => Concat(Ascii("ACK:"), request), replyDelayMilliseconds: 50);
        _ = device.RunAsync(cancel.Token);

        for (int round = 0; round < 5; round++)
        {
            Task<CommResult<byte[]>> first = slave1.RequestAsync(Ascii($"1:R{round}"), new RequestOptions { Timeout = 3000 });
            Task<CommResult<byte[]>> second = slave2.RequestAsync(Ascii($"2:R{round}"), new RequestOptions { Timeout = 3000 });

            CommResult<byte[]> a = await WithinAsync(first, 10000);
            CommResult<byte[]> b = await WithinAsync(second, 10000);
            Assert.True(a.IsSuccess, a.ToString());
            Assert.True(b.IsSuccess, b.ToString());
            Assert.Equal(Ascii($"ACK:1:R{round}"), a.Data);
            Assert.Equal(Ascii($"ACK:2:R{round}"), b.Data);
        }

        Assert.Equal(10, device.Received);
        Assert.Equal(1, device.MaxOutstanding);
    }

    [Fact(Timeout = 30000)]
    public async Task QueueFull_DefaultDropOldest()
    {
        Assert.Equal(QueueFullMode.DropOldest, new SerialChannelConfig().QueueFullMode);

        var factory = new FakeSerialPortHandleFactory();
        await using var channel = CreateChannel(factory, CreateConfig(framing: DelimiterFraming(), receiveQueueCapacity: 2));

        // 第一帧的处理器阻塞派发循环，之后的帧只能排队；队列容量为 2，超出时丢弃最旧的帧。
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<byte[]>();
        int calls = 0;
        channel.FrameReceived += (sender, args) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult(true);
                release.Wait(TimeSpan.FromSeconds(10));
            }

            lock (delivered)
                delivered.Add(args.Data);
        };
        Assert.True((await WithinAsync(channel.ConnectAsync(), 5000)).IsSuccess);
        FakeSerialPortHandle handle = Assert.Single(factory.Handles);

        try
        {
            await WriteRawAsync(handle.Device, Ascii("F1\r\n"));
            await WithinAsync(entered.Task, 3000);

            for (int i = 2; i <= 6; i++)
                await WriteRawAsync(handle.Device, Ascii($"F{i}\r\n"));

            // 队列 [F2, F3] 满后：F4 挤掉 F2，F5 挤掉 F3，F6 挤掉 F4，共丢弃 3 帧；写入端从未被阻塞。
            await WaitUntilAsync(() => channel.Statistics.FramesDropped == 3, 3000);
            Assert.Equal(ConnectionState.Connected, channel.State);
        }
        finally
        {
            release.Set();
        }

        await WaitUntilAsync(() => DeliveredCount(delivered) == 3, 3000);
        List<byte[]> frames;
        lock (delivered)
            frames = new List<byte[]>(delivered);
        Assert.Equal(Ascii("F1"), frames[0]);
        Assert.Equal(Ascii("F5"), frames[1]);
        Assert.Equal(Ascii("F6"), frames[2]);
    }

    [Fact(Timeout = 30000)]
    public async Task Disconnect_ClosesAndReleasesPort()
    {
        var factory = new FakeSerialPortHandleFactory();
        await using var channel = CreateChannel(factory, CreateConfig(framing: DelimiterFraming()));
        Assert.True((await WithinAsync(channel.ConnectAsync(), 5000)).IsSuccess);
        FakeSerialPortHandle handle = Assert.Single(factory.Handles);

        await WithinAsync(channel.DisconnectAsync(), 5000);

        Assert.Equal(ConnectionState.Disconnected, channel.State);
        Assert.True(handle.DisposeCount >= 1, "Disconnect must release the port.");
    }

    [Fact(Timeout = 30000)]
    public async Task Initializer_Hangs_ConnectTimesOutWithinHandshakeTimeout()
    {
        // 握手初始化器永不返回（也不响应取消令牌）：握手时限到期后驱动放弃等待，ConnectAsync 返回 Timeout，端口被释放。
        var factory = new FakeSerialPortHandleFactory();
        var components = new ChannelComponents { Initializer = new NeverReturningInitializer() };
        await using var channel = CreateChannel(factory, CreateConfig(handshakeTimeout: 300), components: components);

        var stopwatch = Stopwatch.StartNew();
        CommResult result = await WithinAsync(channel.ConnectAsync(), 5000);
        stopwatch.Stop();

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 240d, 2300d);
        Assert.Equal(ConnectionState.Disconnected, channel.State);

        FakeSerialPortHandle handle = Assert.Single(factory.Handles);
        await WaitUntilAsync(() => handle.DisposeCount >= 1, 3000);
        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact(Timeout = 30000)]
    public async Task Disconnect_DrainsQueuedFramesWithinDisconnectTimeout()
    {
        // 派发循环被第一帧的处理器阻塞，之后每帧处理约 50 ms；另外三帧留在派发队列中。
        // DisconnectAsync 必须在 DisconnectTimeout 内把这些已收到的帧全部派发完才返回（Task 5 的排空语义）。
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<byte[]>();
        int calls = 0;
        var factory = new FakeSerialPortHandleFactory();
        await using var channel = CreateChannel(factory, CreateConfig(framing: DelimiterFraming(), disconnectTimeout: 1000));
        channel.FrameReceived += (sender, args) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult(true);
                release.Wait(TimeSpan.FromSeconds(10));
            }

            Thread.Sleep(50);
            lock (delivered)
                delivered.Add(args.Data);
        };
        Assert.True((await WithinAsync(channel.ConnectAsync(), 5000)).IsSuccess);
        FakeSerialPortHandle handle = Assert.Single(factory.Handles);

        Task disconnecting;
        try
        {
            await WriteRawAsync(handle.Device, Ascii("F1\r\n"));
            await WithinAsync(entered.Task, 3000);
            await WriteRawAsync(handle.Device, Ascii("F2\r\nF3\r\nF4\r\n"));
            await WaitUntilAsync(() => channel.Statistics.FramesReceived == 4, 3000);
            await Task.Delay(100);   // 让解析循环把 F4 路由进派发队列（路由本身不阻塞）。

            disconnecting = channel.DisconnectAsync();
        }
        finally
        {
            release.Set();
        }

        await WithinAsync(disconnecting, 5000);

        List<byte[]> frames;
        lock (delivered)
            frames = new List<byte[]>(delivered);
        Assert.Equal(4, frames.Count);
        Assert.Equal(Ascii("F1"), frames[0]);
        Assert.Equal(Ascii("F2"), frames[1]);
        Assert.Equal(Ascii("F3"), frames[2]);
        Assert.Equal(Ascii("F4"), frames[3]);
        Assert.Equal(0, channel.Statistics.FramesDropped);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
    }

    [Fact(Timeout = 30000)]
    public void Constructor_DoesNotOpenPort()
    {
        var factory = new FakeSerialPortHandleFactory();
        using var channel = CreateChannel(factory, CreateConfig());

        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
        Assert.Equal("COM3", channel.Name);
    }

    [Fact(Timeout = 30000)]
    public async Task Endpoint_DescribesPortAndBaudRate()
    {
        var logger = new TestLogger();
        var factory = new FakeSerialPortHandleFactory();
        await using var channel = CreateChannel(factory, CreateConfig(framing: DelimiterFraming()),
                                                ((ILoggerFactory)logger).CreateLogger<SerialChannel>());

        Assert.True((await WithinAsync(channel.ConnectAsync(), 5000)).IsSuccess);

        Assert.Contains(logger.GetEntries(LogLevel.Information), entry => entry.Message.Contains("COM3@115200", StringComparison.Ordinal));
    }

    [Fact(Timeout = 30000)]
    public async Task Config_RuntimeUsesConstructionSnapshot()
    {
        // 构造之后修改调用方的配置对象，已创建的通道与之后的重新打开都不受影响（D5 的快照语义）。
        var factory = new FakeSerialPortHandleFactory();
        SerialChannelConfig config = CreateConfig(framing: DelimiterFraming());
        await using var channel = CreateChannel(factory, config);

        config.PortName = "COM9";
        config.BaudRate = 9600;
        config.OpenTimeout = 1;

        Assert.Same(config, channel.Config);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 5000)).IsSuccess);
        SerialChannelConfig used = Assert.Single(factory.Handles).Config;
        Assert.Equal("COM3", used.PortName);
        Assert.Equal(115200, used.BaudRate);
        Assert.Equal(2000, used.OpenTimeout);
    }

    // 永不完成的握手初始化器（不响应取消令牌）：驱动必须在握手时限到期后放弃等待它。
    private sealed class NeverReturningInitializer : IConnectionInitializer
    {
        private readonly TaskCompletionSource<CommResult> never = new TaskCompletionSource<CommResult>();

        public Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken) => never.Task;
    }

    private static int CountConnected(List<ConnectionStateChangedEventArgs> states)
    {
        lock (states)
            return states.Count(args => args.CurrentState == ConnectionState.Connected);
    }

    private static int DeliveredCount(List<byte[]> delivered)
    {
        lock (delivered)
            return delivered.Count;
    }
}
