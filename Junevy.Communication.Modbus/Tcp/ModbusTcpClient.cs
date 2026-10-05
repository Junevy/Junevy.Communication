using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Core.Parsing;
using Junevy.Communication.Modbus.Core.Transports;
using Junevy.Communication.Modbus.Extensions;
using Junevy.Communication.Modbus.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers;
using System.Net.Sockets;

namespace Junevy.Communication.Modbus.Tcp
{
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

        public bool CheckConnection() => IsConnected;

        // ————————————————— 连接钩子 —————————————————

        protected override bool OpenConnection()
        {
            if (!ModbusHelper.VerifyAddress(Config.Address) || !ModbusHelper.VerifyPort(Config.Port))
                return false;

            ResetSocket();

            try
            {
                var result = socket!.BeginConnect(Config.Address, Config.Port, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(Config.ConnectTimeout, true);
                if (!success)
                {
                    socket.Dispose();
                    socket = null;
                    Logger.LogWarning(" [Connect] Connection timed out: {Timeout}ms.", Config.ConnectTimeout);
                    return false;
                }

                socket.EndConnect(result);
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

#if NET8_0_OR_GREATER
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Config.ConnectTimeout);
            try
            {
                var asyncResult = socket!.BeginConnect(Config.Address, Config.Port, null, null);
                await Task.Factory.FromAsync(asyncResult, socket.EndConnect).WaitAsync(timeoutCts.Token);
                stream = new NetworkStream(socket, ownsSocket: false);
                Logger.LogDebug(" [Connect] Connected to {Address}:{Port}.", Config.Address, Config.Port);
                return true;
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
#else
            // net472 无 Task.WaitAsync：退化为同步核心（其 WaitOne 内含 ConnectTimeout 超时），
            // 取消令牌在连接期间不生效，于下一个 I/O 边界生效。
            cancellationToken.ThrowIfCancellationRequested();
            return OpenConnection();
#endif
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

        protected override bool ShouldReconnectAfterFailure(ModbusResult<byte[]> result)
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

            var frame = ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxTcpAduLength);
            try
            {
                if (!FrameBuilder.TryWriteRequestFrame(request, ProtocolType, frame, out int bytesWritten))
                    return false;

                await target.WriteAsync(frame, 0, bytesWritten, cancellationToken);
                LogTx("ModbusTcpClient", new ArraySegment<byte>(frame, 0, bytesWritten).ToArray());
                return true;
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

                ushort pduLength = BinaryExtensions.ToUshort(frame[5], frame[4]);
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
            byte[]? frame = null;
            try
            {
                frame = ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxTcpAduLength);
                var headerResult = await ReceiveExactAsync(frame, 0, 6, cancellationToken);
                if (!headerResult.IsSuccess)
                    return headerResult;

                ushort pduLength = BinaryExtensions.ToUshort(frame[5], frame[4]);
                if (pduLength < 1 || pduLength > 254)
                {
                    Logger.LogError(" [Read] Invalid PDU length: {PduLength}.", pduLength);
                    return ModbusResult<byte[]>.Fail($" [Read] Invalid PDU length: {pduLength}.", ModbusErrorKind.ProtocolViolation);
                }

                int totalLength = 6 + pduLength;
                var payloadResult = await ReceiveExactAsync(frame, 6, pduLength, cancellationToken);
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
                // net8.0 上取消令牌即时生效；net472 上在下一个 I/O 边界生效（读超时兜底靠 socket.ReceiveTimeout）。
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
            socket.ReceiveTimeout = Config.ReadTimeOut;
            socket.SendTimeout = Config.WriteTimeOut;
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
}
