using Junevy.Communication.Channels;
using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Tcp;
using Junevy.Communication.Tcp.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Secs.Hsms;

/// <summary>
/// SECS 设备端（Equipment）收到的消息的处理器：处理一条主消息并返回它的应答，
/// 返回 null 表示"没有应答"（不带 W 位的主消息，或带 W 位但处理器放弃应答——那种情况设备端会中止事务）。
/// </summary>
/// <param name="primary">收到的数据消息。</param>
/// <param name="cancellationToken">停止或释放时被取消；处理器必须响应它，停止才会完成。</param>
/// <returns>应答；不需要应答时返回 null。</returns>
public delegate Task<SecsMessage?> SecsMessageHandler(SecsMessage primary, CancellationToken cancellationToken);

/// <summary>
/// SECS-II / HSMS-SS 的设备端（计划 Task 6，P51-D2、P51-D15、D16）。
/// </summary>
/// <remarks>
/// <para>
/// 设备端监听一个 TCP 端口，等主机来 Select 之后才收发数据消息；状态见 <see cref="State"/>，
/// 收到的消息按到达顺序逐个交给 <see cref="SetHandler"/> 登记的处理器。
/// </para>
/// <para>
/// <strong>停止不是释放</strong>：<see cref="StopAsync"/> 之后可以再 <see cref="StartAsync"/>，行为与第一次相同；
/// <see cref="Dispose"/> 之后永久不可用（<see cref="StartAsync"/> 抛 <see cref="ObjectDisposedException"/>，
/// <see cref="SendAsync"/> 返回 <see cref="SecsError.ConnectionLost"/>）。
/// </para>
/// </remarks>
public sealed partial class HsmsEquipment : IDisposable, IAsyncDisposable
{
    /// <summary>HSMS-SS 是单会话：同一时刻只允许一个主机（计划第 0 节 P51-D9）。</summary>
    private const int SingleSession = 1;

    private readonly HsmsEquipmentSettings settings;
    private readonly HsmsSessionState session;
    private readonly SecsHandlerTable handlers = new SecsHandlerTable();
    private readonly ILogger logger;
    private readonly ILoggerFactory loggerFactory;
    private readonly object sync = new object();

    private ITcpServer? server;
    private TcpClientChannel? client;
    private IByteChannel? current;
    private CancellationTokenSource? running;
    private Task dispatchTail = Task.CompletedTask;
    private readonly object dispatchGate = new object();
    private bool started;
    private bool disposed;

    private HsmsEquipment(HsmsEquipmentSettings settings, HsmsSessionState session, ILoggerFactory loggerFactory)
    {
        this.settings = settings;
        this.session = session;
        this.loggerFactory = loggerFactory;
        logger = loggerFactory.CreateLogger<HsmsEquipment>();
    }

    /// <summary>会话状态（SEMI E37）：<c>NotConnected</c> / <c>NotSelected</c> / <c>Selected</c>。</summary>
    public HsmsState State => session.State;

    /// <summary>状态变化；按发生顺序发出，订阅者抛异常不影响状态机。</summary>
    public event EventHandler<HsmsStateChangedEventArgs>? StateChanged;

    /// <summary>校验选项、创建日志与状态，并取快照。调用方负责释放。</summary>
    /// <param name="options">选项；之后改它不影响已建好的设备。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不记日志。</param>
    /// <returns>设备端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException">任一选项越界或地址非法。</exception>
    public static HsmsEquipment Create(HsmsEquipmentOptions options, ILoggerFactory? loggerFactory = null)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        ILoggerFactory factory = loggerFactory ?? NullLoggerFactory.Instance;
        var state = new HsmsSessionState(factory.CreateLogger<HsmsEquipment>());
        var equipment = new HsmsEquipment(HsmsEquipmentSettings.Validate(options), state, factory);
        state.StateChanged += equipment.RaiseStateChanged;
        return equipment;
    }

    /// <summary>开始监听。已启动时返回 <see cref="CommErrorKind.InvalidRequest"/> 的失败结果。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启动结果。</returns>
    /// <exception cref="ObjectDisposedException">设备端已释放。</exception>
    public async Task<CommResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CancellationTokenSource signals = new CancellationTokenSource();
        lock (sync)
        {
            if (started)
            {
                signals.Dispose();
                return CommResult.Fail("The equipment is already started.", CommErrorKind.InvalidRequest);
            }

            started = true;
            running = signals;
        }

        // 停止与释放取消 signals：进行中的启动（尤其是等 T6 的主动端 Select）随之结束，而不是让停止等它（P51-D19）。
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, signals.Token);
        try
        {
            CommResult result = settings.Mode == HsmsConnectionMode.Passive
                ? await StartListeningAsync(linked.Token).ConfigureAwait(false)
                : await StartConnectingAsync(linked.Token).ConfigureAwait(false);
            if (!result.IsSuccess)
                await StopInternalAsync(signals, CancellationToken.None).ConfigureAwait(false);

            return signals.IsCancellationRequested ? StoppedWhileStarting() : result;
        }
        catch (OperationCanceledException) when (signals.IsCancellationRequested)
        {
            // 停止或释放的那一方负责收尾，这里只报告结果。
            return StoppedWhileStarting();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopInternalAsync(signals, CancellationToken.None).ConfigureAwait(false);
            return CommResult.Fail("Starting the HSMS equipment was cancelled.", CommErrorKind.Cancelled);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Starting the HSMS equipment failed.");
            await StopInternalAsync(signals, CancellationToken.None).ConfigureAwait(false);
            return CommResult.Fail("Starting the HSMS equipment failed.", CommErrorKind.Unspecified, null, ex);
        }
    }

    /// <summary>
    /// 停止监听：已选中时先尽力发一条 <c>Separate.req</c>（不等应答），再断开连接并停止监听。
    /// 之后可以再 <see cref="StartAsync"/>；可重复调用；已释放时直接返回。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>停止结束后的任务。</returns>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? signals;
        lock (sync)
        {
            if (disposed || !started)
                return Task.CompletedTask;

            signals = running;
        }

        return StopInternalAsync(signals, cancellationToken);
    }

    /// <summary>登记一条主消息的处理器；同一 Stream 与 Function 再登记时替换旧的。</summary>
    /// <param name="stream">Stream，0–127。</param>
    /// <param name="function">Function，必须是奇数。</param>
    /// <param name="handler">处理器；为 null 抛 <see cref="ArgumentNullException"/>。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stream"/> 大于 127。</exception>
    /// <exception cref="ArgumentException"><paramref name="function"/> 是偶数（应答不交给处理器）。</exception>
    public void SetHandler(byte stream, byte function, SecsMessageHandler handler)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));
        if (stream > 127)
            throw new ArgumentOutOfRangeException(nameof(stream), stream, "The stream must be between 0 and 127.");
        if (HsmsMessage.IsReplyFunction(function))
            throw new ArgumentException($"The function {function} is a reply, so it is not dispatched to a handler.", nameof(function));

        handlers.Set(stream, function, handler);
    }

    /// <summary>移除一个处理器。</summary>
    /// <param name="stream">Stream。</param>
    /// <param name="function">Function。</param>
    /// <returns>原来有处理器返回 true。</returns>
    public bool RemoveHandler(byte stream, byte function) => handlers.Remove(stream, function);

    /// <summary>停止监听并释放；之后永久不可用。可重复调用。</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>停止监听并释放；之后永久不可用。可重复调用。</summary>
    /// <returns>释放结束后的任务。</returns>
    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? signals;
        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;
            signals = running;
        }

        await StopInternalAsync(signals, CancellationToken.None).ConfigureAwait(false);
        session.Reset("The equipment was disposed.");
    }

    private static CommResult StoppedWhileStarting()
        => CommResult.Fail("The equipment was stopped while it was starting.", CommErrorKind.ConnectionClosed);

    private void ThrowIfDisposed()
    {
        lock (sync)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(HsmsEquipment));
        }
    }

    // 状态只有一个来源（HsmsSessionState）；这里只是把它的事件转发给宿主，并保持订阅者之间互相隔离。
    private void RaiseStateChanged(object? sender, HsmsStateChangedEventArgs args)
        => EventSubscribers.InvokeEach(StateChanged, this, args, logger, nameof(StateChanged));

    // 停止：先告诉处理器"要停了"（它们必须响应令牌），再尽力告知对端，最后断开连接并停止监听或重连。
    // 重复调用安全：第一次把 started 置否，之后的调用直接返回。
    private async Task StopInternalAsync(CancellationTokenSource? signals, CancellationToken cancellationToken)
    {
        ITcpServer? listening;
        TcpClientChannel? connecting;
        IByteChannel? attached;
        lock (sync)
        {
            if (!started)
                return;

            started = false;
            running = null;
            listening = server;
            connecting = client;
            attached = current;
        }

        // 取消但不释放：正在运行的处理器可能还持有这个令牌（计划第 0 节 P51-D19）。
        // 派发在处理器返回后看这个令牌：已取消就不再发送它的应答。
        if (signals != null)
            CancelQuietly(signals);

        if (attached != null && session.State == HsmsState.Selected)
            await SayGoodbyeAsync(attached, cancellationToken).ConfigureAwait(false);

        lock (sync)
        {
            current = null;
            server = null;
            client = null;
        }

        // 等在途的处理器返回：处理器必须响应取消令牌（P51-D19），因此这一等是有界的；
        // 它的应答已在派发里按令牌跳过，这里只保证停止不与处理器并发。
        // 例外：处理器自己调用 StopAsync 时不能等自己，否则死锁。
        if (inDispatch.Value)
            logger.LogDebug("The equipment is stopping from inside a handler; the in-flight dispatch is not awaited.");
        else
            await DrainDispatchAsync().ConfigureAwait(false);

        if (listening != null)
            await listening.StopAsync(cancellationToken).ConfigureAwait(false);

        await ReleaseAsync(listening, connecting).ConfigureAwait(false);
        session.Reset("The equipment was stopped.");
    }

    // 告别：尽力发一条 Separate.req（已选中时、不等应答）；发不出去也照样停止。
    private async Task SayGoodbyeAsync(IByteChannel attached, CancellationToken cancellationToken)
    {
        try
        {
            await attached
                .SendAsync(HsmsControl.SeparateRequest(HsmsSystemBytes.Next()).ToArray(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Sending Separate.req while stopping failed; the connection is closed anyway.");
        }
    }

    private static void CancelQuietly(CancellationTokenSource signals)
    {
        try
        {
            signals.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 令牌源已被释放：没有需要取消的订阅者。
        }
    }
}
