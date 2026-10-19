using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.DebugAssistant.Simulators.Hosting;

/// <summary>
/// 把一个引擎挂到 TCP 服务端、非定向 UDP 或串口上（计划第 8.1 节）。配置的类型决定传输方式。
/// 请求按到达顺序交给引擎，应答经同一个队列按同样的顺序发送；故障注入的延迟在发送前执行，因此慢设备会让后续应答也等待，
/// 与真实的单线程设备一致。
/// </summary>
public sealed class SimulatorHost(IChannelConfig config, ISimulatorEngine engine, FaultInjector faults, ILoggerFactory? loggerFactory = null) : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    private readonly ILogger<SimulatorHost> _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SimulatorHost>();
    private readonly SimulatorRequestProcessor _processor = new(engine, faults);

    /// <summary>UDP 按对端保存引擎的会话状态。</summary>
    private readonly ConcurrentDictionary<string, Dictionary<string, object?>> _datagramStates = new();

    private readonly Dictionary<string, object?> _serialState = [];
    private object? _listener;
    private CancellationTokenSource? _cts;
    private Channel<Func<CancellationToken, Task>>? _outbox;
    private Task? _sender;

    /// <summary>每处理一个请求触发一次（请求日志）。在后台线程触发，界面需要自行切换线程。</summary>
    public event EventHandler<SimulatorRequestEventArgs>? RequestHandled;

    /// <summary>是否在运行。</summary>
    public bool IsRunning => _listener is not null;

    /// <summary>传输方式，由配置的类型决定；其他类型的配置被拒绝。</summary>
    public SimulatorTransport Transport => config switch
    {
        TcpServerConfig => SimulatorTransport.Tcp,
        UdpChannelConfig => SimulatorTransport.Udp,
        SerialChannelConfig => SimulatorTransport.Serial,
        _ => throw new ArgumentException("模拟器只支持 TCP 服务端、UDP 与串口配置。", nameof(config)),
    };

    /// <summary>启动监听。已经在运行时返回失败。</summary>
    public async Task<CommResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return CommResult.Fail("模拟器已经在运行。", CommErrorKind.InvalidRequest);
        }

        _ = Transport;
        _cts = new CancellationTokenSource();
        _outbox = Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });
        _sender = RunSenderAsync(_outbox.Reader, _cts.Token);

        CommResult started = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return started;
    }

    /// <summary>停止监听，并等待已排队的应答发送完或被取消。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _outbox?.Writer.TryComplete();
        if (_cts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (_sender is { } sender)
        {
            try
            {
                await sender.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 停止时取消发送循环是正常结束。
            }
        }

        switch (_listener)
        {
            case TcpServer server:
                await server.StopAsync(cancellationToken).ConfigureAwait(false);
                break;
        }

        if (_listener is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        _listener = null;
        _sender = null;
        _outbox = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private Task<CommResult> OpenAsync(CancellationToken cancellationToken) => config switch
    {
        TcpServerConfig tcp => OpenTcpAsync(tcp, cancellationToken),
        UdpChannelConfig udp => OpenUdpAsync(udp, cancellationToken),
        _ => OpenSerialAsync((SerialChannelConfig)config, cancellationToken),
    };

    private async Task<CommResult> OpenTcpAsync(TcpServerConfig tcp, CancellationToken cancellationToken)
    {
        var components = new TcpChannelComponents { FrameCodec = engine.CreateRequestCodec(SimulatorTransport.Tcp) };
        var server = new TcpServer(tcp, _loggerFactory.CreateLogger<TcpServer>(), components);
        server.FrameReceived += (_, e) => OnTcpFrame(server, e.Session, e.Data);
        _listener = server;
        return await server.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommResult> OpenUdpAsync(UdpChannelConfig udp, CancellationToken cancellationToken)
    {
        var channel = new UdpChannel(udp, _loggerFactory.CreateLogger<UdpChannel>(), new ChannelComponents { FrameCodec = engine.CreateRequestCodec(SimulatorTransport.Udp) });
        channel.FrameReceived += (_, e) => OnDatagram(channel, e);
        _listener = channel;
        return await channel.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommResult> OpenSerialAsync(SerialChannelConfig serial, CancellationToken cancellationToken)
    {
        var components = new ChannelComponents { FrameCodec = engine.CreateRequestCodec(SimulatorTransport.Serial) };
        IClientChannel port = new SerialChannelCreator().Create(null, serial, components, _loggerFactory);
        port.FrameReceived += (_, e) => OnSerialFrame(port, e);
        _listener = port;
        return await port.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnTcpFrame(TcpServer server, ITcpSession session, byte[] data)
    {
        string remote = session.RemoteEndPoint.ToString();
        SimulatorOutcome outcome = _processor.Process(data, new SimulatorConnection(session.Id, remote, session.Items));
        Notify(data, remote, outcome);
        Enqueue(outcome, payload => server.SendAsync(session.Id, payload), () => session.CloseAsync());
    }

    private void OnDatagram(UdpChannel channel, FrameReceivedEventArgs e)
    {
        if (e.RemoteEndPoint is not IPEndPoint remote)
        {
            return;
        }

        string key = remote.ToString();
        Dictionary<string, object?> items = _datagramStates.GetOrAdd(key, _ => []);
        SimulatorOutcome outcome = _processor.Process(e.Data, new SimulatorConnection(key.GetHashCode(), key, items));
        Notify(e.Data, key, outcome);
        Enqueue(
            outcome,
            payload => channel.SendToAsync(remote, payload, CancellationToken.None),
            () =>
            {
                // 数据报没有连接；"断开"表示忘记这个对端的会话状态。
                _datagramStates.TryRemove(key, out _);
                return Task.CompletedTask;
            });
    }

    private void OnSerialFrame(IClientChannel port, FrameReceivedEventArgs e)
    {
        SimulatorOutcome outcome = _processor.Process(e.Data, new SimulatorConnection(0, null, _serialState));
        Notify(e.Data, null, outcome);
        Enqueue(
            outcome,
            payload => port.SendAsync(payload, CancellationToken.None),
            async () =>
            {
                // 串口没有会话：断开表示关闭并重新打开端口，对端看到的是一段沉默。
                await port.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                await port.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            });
    }

    private void Notify(byte[] request, string? remote, SimulatorOutcome outcome) =>
        RequestHandled?.Invoke(this, new SimulatorRequestEventArgs(DateTimeOffset.Now, remote, request, outcome.Kind, outcome.Response, outcome.Summary));

    /// <summary>把一个应答排入发送队列：延迟、发送或断开，都在发送循环里按顺序执行。</summary>
    private void Enqueue(SimulatorOutcome outcome, Func<byte[], Task> send, Func<Task> disconnect)
    {
        _outbox?.Writer.TryWrite(async ct =>
        {
            if (outcome.DelayMilliseconds > 0)
            {
                await Task.Delay(outcome.DelayMilliseconds, ct).ConfigureAwait(false);
            }

            switch (outcome.Kind)
            {
                case SimulatorReplyKind.Reply:
                    await send(outcome.Response!).ConfigureAwait(false);
                    break;
                case SimulatorReplyKind.Disconnect:
                    await disconnect().ConfigureAwait(false);
                    break;
            }
        });
    }

    private async Task RunSenderAsync(ChannelReader<Func<CancellationToken, Task>> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (Func<CancellationToken, Task> action in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await action(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "模拟器发送应答失败。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止时正常结束。
        }
    }
}
