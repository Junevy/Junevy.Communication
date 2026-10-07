using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Core.Parsing;
using Junevy.Communication.Modbus.Core.Transports;
using Junevy.Communication.Modbus.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;

namespace Junevy.Communication.Modbus.Tcp;

/// <summary>
/// Modbus TCP 客户端。请求/重试/重连骨架由 <see cref="ModbusTransportBase"/> 提供，
/// 本类只实现 TCP 协议相关的连接与收发（MBAP 头封装、6 字节头 + PDU 读取）。
/// </summary>
public sealed class ModbusTcpClient : ModbusTransportBase
{
    private Socket? socket;
    private NetworkStream? stream;

    public ModbusTcpClientConfig Config { get; private set; }
    public override bool IsConnected => !disposed && IsSocketConnected(socket);
    public override ModbusProtocolType ProtocolType => ModbusProtocolType.TCP;

    public ModbusTcpClient(ModbusTcpClientConfig config)
        : this(config, NullLogger<ModbusTcpClient>.Instance, new TcpProtocolParser())
    {
    }

    public ModbusTcpClient(ModbusTcpClientConfig config, ILogger<ModbusTcpClient> logger)
        : this(config, logger, new TcpProtocolParser())
    {
    }

    public ModbusTcpClient(ModbusTcpClientConfig config, ILogger<ModbusTcpClient> logger, IResponseParser responseParser)
        : this(config, logger, responseParser, new ModbusFrameBuilder())
    {
    }

    public ModbusTcpClient(
        ModbusTcpClientConfig config,
        ILogger<ModbusTcpClient> logger,
        IResponseParser responseParser,
        IModbusFrameBuilder frameBuilder)
        : base(logger, responseParser, frameBuilder)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        socket = CreateSocket();
    }

    // ————————————————— 连接钩子 —————————————————

    protected override bool OpenConnection()
    {
        if (!ModbusHelper.VerifyAddress(Config.Address) || !ModbusHelper.VerifyPort(Config.Port))
            return false;

        ResetSocket();

        try
        {
            var result = socket!.BeginConnect(Config.Address, Config.Port, null, null);
            try
            {
                bool success = result.AsyncWaitHandle.WaitOne(Config.ConnectTimeout, true);
                if (!success)
                {
                    socket.Dispose();
                    socket = null;
                    Logger.LogWarning(" [Connect] Connection timed out: {Timeout}ms.", Config.ConnectTimeout);
                    return false;
                }

                socket.EndConnect(result);
            }
            finally
            {
                // AsyncWaitHandle 是一次性内核句柄，不释放会泄漏；成功路径同样需要释放
                result.AsyncWaitHandle.Close();
            }

            stream = new NetworkStream(socket, ownsSocket: false);
            Logger.LogDebug(" [Connect] Connected to {Address}:{Port}.", Config.Address, Config.Port);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, " [Connect] Connection failed.");
            socket?.Dispose();
            socket = null;
            return false;
        }
    }

    protected override async Task<bool> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (!ModbusHelper.VerifyAddress(Config.Address) || !ModbusHelper.VerifyPort(Config.Port))
            return false;

        ResetSocket();
        var connectSocket = socket!;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Config.ConnectTimeout);
        try
        {
#if NET8_0_OR_GREATER
            await connectSocket.ConnectAsync(Config.Address, Config.Port, timeoutCts.Token).ConfigureAwait(false);
#else
            // net472 无 Socket.ConnectAsync(CancellationToken)：用 APM 的 Task 包装 + WhenAny 实现真异步，
            // 调用线程立即返回。超时时销毁 socket 中止底层连接，并观察其最终异常以免"未观察的任务异常"。
            var connectTask = Task.Factory.FromAsync(
                connectSocket.BeginConnect(Config.Address, Config.Port, null, null),
                connectSocket.EndConnect);
            var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
            var finished = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
            if (finished != connectTask)
            {
                connectSocket.Dispose();
                connectTask.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                timeoutCts.Token.ThrowIfCancellationRequested();
            }

            await connectTask.ConfigureAwait(false);
#endif
            stream = new NetworkStream(connectSocket, ownsSocket: false);
            Logger.LogDebug(" [ConnectAsync] Connected to {Address}:{Port}.", Config.Address, Config.Port);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户取消区别于连接超时：向上传播，由基类请求循环归为 Cancelled
            InvalidateConnection();
            throw;
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning(" [ConnectAsync] Connection timed out: {Timeout}ms.", Config.ConnectTimeout);
            InvalidateConnection();
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, " [ConnectAsync] Connection failed.");
            InvalidateConnection();
            return false;
        }
    }

    protected override void CloseConnection()
    {
        try
        {
            if (socket?.Connected ?? false)
                socket.Disconnect(false);

            stream?.Dispose();
            stream = null;
            socket?.Dispose();
            socket = null;
            Logger.LogDebug(" [Disconnect] Disconnected from {Address}:{Port}.", Config.Address, Config.Port);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, " [Disconnect] Disconnect failed.");
        }
    }

    protected override void InvalidateConnection()
    {
        try
        {
            stream?.Dispose();
            socket?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, " [MarkConnectionFaulted] Error disposing socket.");
        }
        finally
        {
            stream = null;
            socket = null;
        }
    }

    protected override void DisposeConnection()
    {
        stream?.Dispose();
        stream = null;
        socket?.Dispose();
        socket = null;
    }

    // ————————————————— 配置转发 —————————————————

    protected override bool ReconnectEnabled => Config.Reconnect;
    protected override int RetryCount => Config.RetryCount;
    protected override int RetryInterval => Config.RetryInterval;
    protected override bool AssignsTransactionId => true;

    // ————————————————— 消息/日志文本钩子 —————————————————

    protected override string GetNotConnectedMessage(bool isAsync)
        => isAsync ? " [RequestAsync] Not connected." : " [Request] Not connected.";

    protected override string GetSendFailedMessage(bool isAsync)
        => isAsync ? " [RequestAsync] Send failed." : " [Request] Send failed.";

    protected override string GetRequestFailedLogText(bool isAsync)
        => isAsync ? " [RequestAsync] Request failed." : " [Request] Request failed.";

    protected override void LogReconnectAttempt(bool isAsync)
    {
        if (isAsync)
            Logger.LogInformation(" [ReconnectAsync] TCP connection is not available. Reconnecting to {Address}:{Port}.", Config.Address, Config.Port);
        else
            Logger.LogInformation(" [Reconnect] TCP connection is not available. Reconnecting to {Address}:{Port}.", Config.Address, Config.Port);
    }

    protected override void LogReconnectFailed(Exception ex, bool isAsync)
    {
        if (isAsync)
            Logger.LogWarning(ex, " [ReconnectAsync] TCP reconnect failed.");
        else
            Logger.LogWarning(ex, " [Reconnect] TCP reconnect failed.");
    }

    protected override ModbusResult<byte[]> CreateInvalidRequestResult(ModbusRequest request, bool isAsync)
    {
        if (isAsync)
        {
            Logger.LogWarning(" [RequestAsync] Invalid request: {@Request}.", request);
            return ModbusResult<byte[]>.Fail(" [RequestAsync] Invalid request.", ModbusErrorKind.InvalidRequest);
        }

        Logger.LogWarning(" [Request] Invalid request: {@Request}.", request);
        return ModbusResult<byte[]>.Fail(" [Request] Invalid request.", ModbusErrorKind.InvalidRequest);
    }

    protected override bool RequiresNewConnection(ModbusResult<byte[]> result)
        => result.ErrorKind is ModbusErrorKind.Timeout or ModbusErrorKind.ConnectionClosed;

    // ————————————————— 收发钩子 —————————————————

    protected override bool SendFrame(ModbusRequest request)
    {
        byte[]? frame = null;
        try
        {
            frame = ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxTcpAduLength);
            if (!FrameBuilder.TryWriteRequestFrame(request, ProtocolType, frame, out int bytesWritten))
                return false;

            int totalSent = 0;
            while (totalSent < bytesWritten)
            {
                int sent = socket!.Send(frame, totalSent, bytesWritten - totalSent, SocketFlags.None);
                totalSent += sent;
                Logger.LogDebug(" [Send] Total sent: {Total}, current: {Current}.", totalSent, sent);

                if (sent == 0)
                    return false;
            }

            LogTx("ModbusTcpClient", new ArraySegment<byte>(frame, 0, bytesWritten).ToArray());
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            Logger.LogError(" [Send] Send timed out.");
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, " [Send] Send failed.");
            throw;
        }
        finally
        {
            if (frame != null)
                ArrayPool<byte>.Shared.Return(frame);
        }
    }

    protected override async Task<bool> SendFrameAsync(ModbusRequest request, CancellationToken cancellationToken)
    {
        var target = stream;
        if (target is null) return false;

        // Socket.SendTimeout 只约束同步 I/O；异步路径用链接取消源实现整帧写超时。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Config.WriteTimeout);
#if !NET8_0_OR_GREATER
        // net472 上 NetworkStream.WriteAsync 不响应取消令牌：取消时销毁 socket 中止挂起的写。
        var activeSocket = socket;
        using var abortRegistration = timeoutCts.Token.Register(() => activeSocket?.Dispose());
#endif
        var frame = ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxTcpAduLength);
        try
        {
            if (!FrameBuilder.TryWriteRequestFrame(request, ProtocolType, frame, out int bytesWritten))
                return false;

            await target.WriteAsync(frame, 0, bytesWritten, timeoutCts.Token);
            LogTx("ModbusTcpClient", new ArraySegment<byte>(frame, 0, bytesWritten).ToArray());
            return true;
        }
        catch (Exception ex) when (timeoutCts.IsCancellationRequested && (ex is OperationCanceledException || ex is ObjectDisposedException || ex is IOException || ex is SocketException))
        {
            if (cancellationToken.IsCancellationRequested)
                throw;  // 用户取消：由基类请求循环归为 Cancelled

            // 与同步路径 SocketError.TimedOut 时 return false 的行为一致，
            // 基类随后标记 ConnectionClosed 并销毁连接。
            Logger.LogError(" [SendAsync] Write timed out: {Timeout}ms.", Config.WriteTimeout);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;  // 由基类请求循环归为 Cancelled
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Logger.LogError(ex, " [SendAsync] Send failed.");
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    protected override ModbusResult<byte[]> ReceiveFrame(ModbusRequest request)
    {
        byte[]? frame = null;
        try
        {
            frame = ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxTcpAduLength);
            var headerResult = ReceiveExact(frame, 0, 6);
            if (!headerResult.IsSuccess)
                return headerResult;

            ushort pduLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4, 2));
            if (pduLength < 1 || pduLength > 254)
            {
                Logger.LogError(" [Read] Invalid PDU length: {PduLength}.", pduLength);
                return ModbusResult<byte[]>.Fail($" [Read] Invalid PDU length: {pduLength}.", ModbusErrorKind.ProtocolViolation);
            }

            int totalLength = 6 + pduLength;
            var payloadResult = ReceiveExact(frame, 6, pduLength);
            if (!payloadResult.IsSuccess)
                return payloadResult;

            var data = new ReadOnlyMemory<byte>(frame, 0, totalLength);
            LogRx("ModbusTcpClient", data.Span);

            var parsed = ResponseParser.ParseResponse(data, request);
            return parsed.IsSuccess
                ? ModbusResult<byte[]>.Success(parsed.Data.ToArray())
                : ModbusResult<byte[]>.Fail(parsed.ErrorMessage ?? " [Read] Parse error.", parsed.ErrorKind, data.ToArray());
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            Logger.LogError(" [Read] Read timed out.");
            return ModbusResult<byte[]>.Fail(" [Read] Read timeout.", ModbusErrorKind.Timeout);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, " [Read] Read failed.");
            throw;
        }
        finally
        {
            if (frame != null)
                ArrayPool<byte>.Shared.Return(frame);
        }
    }

    protected override async Task<ModbusResult<byte[]>> ReceiveFrameAsync(ModbusRequest request, CancellationToken cancellationToken)
    {
        // Socket.ReceiveTimeout 只约束同步 I/O；异步路径用链接取消源实现整帧读超时
        // （从进入本方法到 6 字节头加负载完整读出的总时限，而非单次 ReadAsync 的时限）。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Config.ReadTimeout);
#if !NET8_0_OR_GREATER
        // net472 上 NetworkStream.ReadAsync 不响应取消令牌：取消时销毁 socket 中止挂起的读，
        // ReadAsync 随之抛出 ObjectDisposedException/IOException，由下方超时 catch 归类。
        var activeSocket = socket;
        using var abortRegistration = timeoutCts.Token.Register(() => activeSocket?.Dispose());
#endif
        byte[]? frame = null;
        try
        {
            frame = ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxTcpAduLength);
            var headerResult = await ReceiveExactAsync(frame, 0, 6, timeoutCts.Token);
            if (!headerResult.IsSuccess)
                return headerResult;

            ushort pduLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4, 2));
            if (pduLength < 1 || pduLength > 254)
            {
                Logger.LogError(" [Read] Invalid PDU length: {PduLength}.", pduLength);
                return ModbusResult<byte[]>.Fail($" [Read] Invalid PDU length: {pduLength}.", ModbusErrorKind.ProtocolViolation);
            }

            int totalLength = 6 + pduLength;
            var payloadResult = await ReceiveExactAsync(frame, 6, pduLength, timeoutCts.Token);
            if (!payloadResult.IsSuccess)
                return payloadResult;

            var data = new ReadOnlyMemory<byte>(frame, 0, totalLength);
            LogRx("ModbusTcpClient", data.Span);

            var parsed = ResponseParser.ParseResponse(data, request);
            return parsed.IsSuccess
                ? ModbusResult<byte[]>.Success(parsed.Data.ToArray())
                : ModbusResult<byte[]>.Fail(parsed.ErrorMessage ?? " [Read] Parse error.", parsed.ErrorKind, data.ToArray());
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            Logger.LogError(" [Read] Read timed out.");
            return ModbusResult<byte[]>.Fail(" [Read] Read timeout.", ModbusErrorKind.Timeout);
        }
        catch (Exception ex) when (timeoutCts.IsCancellationRequested && (ex is OperationCanceledException || ex is ObjectDisposedException || ex is IOException || ex is SocketException))
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            Logger.LogError(" [ReadAsync] Read timed out: {Timeout}ms.", Config.ReadTimeout);
            return ModbusResult<byte[]>.Fail(" [ReadAsync] Read timeout.", ModbusErrorKind.Timeout);
        }
        catch (OperationCanceledException)
        {
            throw;  // 由基类请求循环归为 Cancelled
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, " [Read] Read failed.");
            throw;
        }
        finally
        {
            if (frame != null)
                ArrayPool<byte>.Shared.Return(frame);
        }
    }

    private async Task<ModbusResult<byte[]>> ReceiveExactAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var target = stream;
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await target.ReadAsync(buffer, offset + totalRead, count - totalRead, cancellationToken);
            if (read == 0)
                return ModbusResult<byte[]>.Fail(" [Read] Connection closed by remote.", ModbusErrorKind.ConnectionClosed);

            totalRead += read;
        }

        return ModbusResult<byte[]>.Success(Array.Empty<byte>());
    }

    // ————————————————— TCP 私有辅助 —————————————————

    private ModbusResult<byte[]> ReceiveExact(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = socket!.Receive(buffer, offset + totalRead, count - totalRead, SocketFlags.None);
            if (read == 0)
                return ModbusResult<byte[]>.Fail(" [Read] Connection closed by remote.", ModbusErrorKind.ConnectionClosed);

            totalRead += read;
        }

        return ModbusResult<byte[]>.Success(Array.Empty<byte>());
    }

    private void ResetSocket()
    {
        stream?.Dispose();
        stream = null;
        socket?.Dispose();
        socket = CreateSocket();
        socket.ReceiveTimeout = Config.ReadTimeout;
        socket.SendTimeout = Config.WriteTimeout;
        socket.NoDelay = true;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }

    private static Socket CreateSocket()
        => new Socket(AddressFamily.InterNetwork, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);

    private static bool IsSocketConnected(Socket? target)
    {
        if (target == null || !target.Connected)
            return false;

        try
        {
            return !(target.Poll(0, SelectMode.SelectRead) && target.Available == 0);
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
