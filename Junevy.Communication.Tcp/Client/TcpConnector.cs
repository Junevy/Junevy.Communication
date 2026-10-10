using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Tcp.Client;

/// <summary>
/// TCP 连接器（内部）：解析地址，在连接总时限内依次尝试各地址，应用套接字选项，返回已连接的 socket（计划 10.2）。
/// 构造时复制全部配置值，之后不再读取调用方的配置对象。
/// </summary>
internal sealed class TcpConnector
{
    private readonly string host;
    private readonly int port;
    private readonly IPAddress? localAddress;
    private readonly int localPort;
    private readonly int connectTimeout;
    private readonly TcpSocketOptions socketOptions;
    private readonly ILogger logger;
    private readonly object pendingSync = new object();
    private Socket? pending;

    /// <summary>
    /// 创建连接器并复制配置值。
    /// </summary>
    /// <param name="host">远端主机名或 IP 地址。</param>
    /// <param name="port">远端端口。</param>
    /// <param name="localAddress">绑定的本地地址；为 null 时不指定。</param>
    /// <param name="localPort">绑定的本地端口；0 表示由系统分配。</param>
    /// <param name="connectTimeout">连接总时限（毫秒），必须为正。</param>
    /// <param name="socketOptions">套接字选项（复制）。</param>
    /// <param name="logger">日志记录器。</param>
    public TcpConnector(string host, int port, IPAddress? localAddress, int localPort, int connectTimeout, TcpSocketOptions socketOptions,
                        ILogger logger)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.port = port;
        this.localAddress = localAddress;
        this.localPort = localPort;
        this.connectTimeout = connectTimeout;
        this.socketOptions = CopyOf(socketOptions ?? throw new ArgumentNullException(nameof(socketOptions)));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 在总时限内建立连接。超时返回 <c>Timeout</c>；连接被拒绝、主机不可达、DNS 失败返回 <c>ConnectionClosed</c>（消息包含 <c>SocketError</c>）。
    /// 用户取消时抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>已连接的 socket（所有权交给调用方），或失败结果。</returns>
    public async Task<CommResult<Socket>> ConnectAsync(CancellationToken cancellationToken)
    {
        using (TimeoutScope scope = TimeoutScope.Start(connectTimeout, cancellationToken, AbortPending))
        {
            try
            {
                return await ConnectWithinBudgetAsync(scope).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (scope.IsTimedOut)
            {
                logger.LogDebug("Connecting to {Host}:{Port} did not complete within {Timeout} ms.", host, port, connectTimeout);
                return CommResult<Socket>.Fail($"Connecting to {host}:{port} did not complete within {connectTimeout} ms.", CommErrorKind.Timeout);
            }
        }
    }

    // 解析地址后依次尝试；每次尝试之前检查总时限，耗尽时直接抛出取消，由 ConnectAsync 归类。
    private async Task<CommResult<Socket>> ConnectWithinBudgetAsync(TimeoutScope scope)
    {
        CommResult<IPAddress[]> resolved = await ResolveAsync(scope.Token).ConfigureAwait(false);
        if (!resolved.IsSuccess)
            return resolved.As<Socket>();

        CommResult<Socket>? last = null;
        foreach (IPAddress address in resolved.Data!)
        {
            scope.Token.ThrowIfCancellationRequested();
            CommResult<Socket> attempt = await TryConnectAsync(address, scope).ConfigureAwait(false);
            if (attempt.IsSuccess)
                return attempt;

            last = attempt;
        }

        return last ?? CommResult<Socket>.Fail($"No usable address was found for {host}.", CommErrorKind.ConnectionClosed);
    }

    // IP 字面量直接使用；否则做 DNS 解析。IPv4 排在 IPv6 之前；指定了本地地址时只保留同族地址。
    private async Task<CommResult<IPAddress[]>> ResolveAsync(CancellationToken token)
    {
        IPAddress[] resolved;
        if (IPAddress.TryParse(host, out IPAddress? literal) && literal != null)
        {
            resolved = new[] { literal };
        }
        else
        {
            try
            {
                resolved = await LookupAsync(host, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                token.ThrowIfCancellationRequested();
                return CommResult<IPAddress[]>.Fail($"Resolving {host} failed: {DescribeFailure(ex)}.", CommErrorKind.ConnectionClosed, null, ex);
            }
        }

        IPAddress[] ordered = resolved.Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Concat(resolved.Where(address => address.AddressFamily != AddressFamily.InterNetwork))
            .ToArray();

        IPAddress? local = localAddress;
        if (local != null)
        {
            AddressFamily family = local.AddressFamily;
            ordered = ordered.Where(address => address.AddressFamily == family).ToArray();
        }

        if (ordered.Length == 0)
            return CommResult<IPAddress[]>.Fail($"No usable address was found for {host}.", CommErrorKind.ConnectionClosed);

        return CommResult<IPAddress[]>.Success(ordered);
    }

#if NET8_0_OR_GREATER
    private static Task<IPAddress[]> LookupAsync(string host, CancellationToken token)
        => Dns.GetHostAddressesAsync(host, token);
#else
    // net472 的 Dns 没有可取消的重载：与取消令牌竞速。超时后丢弃解析结果，并观察其异常。
    private static async Task<IPAddress[]> LookupAsync(string host, CancellationToken token)
    {
        Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(host);
        Task cancelled = Task.Delay(Timeout.Infinite, token);
        Task finished = await Task.WhenAny(lookup, cancelled).ConfigureAwait(false);
        if (!ReferenceEquals(finished, lookup))
        {
            ObserveFault(lookup);
            token.ThrowIfCancellationRequested();
        }

        return await lookup.ConfigureAwait(false);
    }
#endif

    // 尝试连接一个地址。socket 在连接期间登记为待定：总时限耗尽时由 AbortPending 销毁，使挂起的连接中止。
    private async Task<CommResult<Socket>> TryConnectAsync(IPAddress address, TimeoutScope scope)
    {
        var endPoint = new IPEndPoint(address, port);
        Socket? socket = null;
        try
        {
            socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            SetPending(socket);
            ConfigureSocket(socket);
            await ConnectSocketAsync(socket, endPoint, scope.Token).ConfigureAwait(false);
            return Handover(socket, scope);
        }
        catch (Exception ex)
        {
            if (socket != null)
            {
                TakePending(socket);
                socket.Dispose();
            }

            // 总时限耗尽或用户取消引起的异常转换为取消，由 ConnectAsync 按超时或取消归类。
            scope.Token.ThrowIfCancellationRequested();
            return ConnectFailure(endPoint, ex);
        }
    }

    // 把已连接的 socket 交给调用方。总时限可能恰好在连接完成之后耗尽：此时 AbortPending 已经或即将销毁该 socket，因此视为取消。
    private CommResult<Socket> Handover(Socket socket, TimeoutScope scope)
    {
        if (!TakePending(socket) || scope.Token.IsCancellationRequested)
            throw new OperationCanceledException(scope.Token);

        return CommResult<Socket>.Success(socket);
    }

    private void ConfigureSocket(Socket socket)
    {
        socket.NoDelay = socketOptions.NoDelay;
        if (socketOptions.ReceiveBufferSize > 0)
            socket.ReceiveBufferSize = socketOptions.ReceiveBufferSize;
        if (socketOptions.SendBufferSize > 0)
            socket.SendBufferSize = socketOptions.SendBufferSize;
        if (socketOptions.LingerTime >= 0)
            socket.LingerState = new LingerOption(true, socketOptions.LingerTime);

        ApplyKeepAlive(socket);

        if (localAddress != null || localPort > 0)
        {
            IPAddress bindAddress = localAddress
                ?? (socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any);
            socket.Bind(new IPEndPoint(bindAddress, localPort));
        }
    }

    // 保活只是连接的调优项：设置失败时记录警告，连接继续使用系统默认值。
    private void ApplyKeepAlive(Socket socket)
    {
        TcpKeepAliveOptions keepAlive = socketOptions.KeepAlive;
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, keepAlive.Enabled);
            if (!keepAlive.Enabled)
                return;

#if NET8_0_OR_GREATER
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, ToWholeSeconds(keepAlive.Time));
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, ToWholeSeconds(keepAlive.Interval));
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, keepAlive.RetryCount);
#else
            // net472 没有按秒的选项，只能经 SIO_KEEPALIVE_VALS 设置 {onoff, 空闲毫秒, 间隔毫秒}（均为小端 uint）。
            // 重试次数无法设置，由系统决定（Windows 默认 10 次），RetryCount 在此不生效。
            byte[] values = new byte[12];
            WriteUInt32LittleEndian(values, 0, 1);
            WriteUInt32LittleEndian(values, 4, (uint)keepAlive.Time);
            WriteUInt32LittleEndian(values, 8, (uint)keepAlive.Interval);
            socket.IOControl(IOControlCode.KeepAliveValues, values, null);
#endif
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "Applying the TCP keep-alive options to {Host}:{Port} failed; the system defaults are used.", host, port);
        }
    }

#if NET8_0_OR_GREATER
    // 毫秒向上取整为秒（最小 1 秒）。
    private static int ToWholeSeconds(int milliseconds) => (int)((milliseconds + 999L) / 1000L);

    private static Task ConnectSocketAsync(Socket socket, IPEndPoint endPoint, CancellationToken token)
        => socket.ConnectAsync(endPoint, token).AsTask();
#else
    // net472 的 Socket 没有可取消的 ConnectAsync：把 BeginConnect 包装为任务，与取消令牌竞速（照搬 ModbusTcpClient.OpenConnectionAsync）。
    // 取消或超时时由调用方销毁 socket，使挂起的连接中止；被中止任务的异常在此观察，避免未观察的异常。
    private static async Task ConnectSocketAsync(Socket socket, IPEndPoint endPoint, CancellationToken token)
    {
        Task connect = Task.Factory.FromAsync(socket.BeginConnect(endPoint, null, null), socket.EndConnect);
        Task cancelled = Task.Delay(Timeout.Infinite, token);
        Task finished = await Task.WhenAny(connect, cancelled).ConfigureAwait(false);
        if (!ReferenceEquals(finished, connect))
        {
            ObserveFault(connect);
            token.ThrowIfCancellationRequested();
        }

        await connect.ConfigureAwait(false);
    }

    private static void WriteUInt32LittleEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void ObserveFault(Task task)
        => task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
#endif

    private void SetPending(Socket socket)
    {
        lock (pendingSync)
            pending = socket;
    }

    // 取回待定 socket 的所有权。返回 false 表示总时限已耗尽，socket 已被 AbortPending 接管并销毁。
    private bool TakePending(Socket socket)
    {
        lock (pendingSync)
        {
            if (!ReferenceEquals(pending, socket))
                return false;

            pending = null;
            return true;
        }
    }

    // 总时限耗尽或用户取消时由 TimeoutScope 调用：销毁当前待定的 socket。
    private void AbortPending()
    {
        Socket? current;
        lock (pendingSync)
        {
            current = pending;
            pending = null;
        }

        current?.Dispose();
    }

    private static CommResult<Socket> ConnectFailure(IPEndPoint endPoint, Exception ex)
    {
        if (ex is SocketException socketException)
        {
            CommErrorKind kind = socketException.SocketErrorCode == SocketError.TimedOut ? CommErrorKind.Timeout : CommErrorKind.ConnectionClosed;
            return CommResult<Socket>.Fail($"Connecting to {endPoint} failed: {socketException.SocketErrorCode}.", kind, null, ex);
        }

        return CommResult<Socket>.Fail($"Connecting to {endPoint} failed unexpectedly.", CommErrorKind.Unspecified, null, ex);
    }

    private static string DescribeFailure(Exception ex)
        => ex is SocketException socketException ? socketException.SocketErrorCode.ToString() : ex.GetType().Name;

    private static TcpSocketOptions CopyOf(TcpSocketOptions source)
        => new TcpSocketOptions
        {
            NoDelay = source.NoDelay,
            ReceiveBufferSize = source.ReceiveBufferSize,
            SendBufferSize = source.SendBufferSize,
            LingerTime = source.LingerTime,
            KeepAlive = new TcpKeepAliveOptions
            {
                Enabled = source.KeepAlive.Enabled,
                Time = source.KeepAlive.Time,
                Interval = source.KeepAlive.Interval,
                RetryCount = source.KeepAlive.RetryCount,
            },
        };
}
