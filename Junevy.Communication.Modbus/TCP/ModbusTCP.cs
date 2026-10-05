using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Core.Parsing;
using Junevy.Communication.Modbus.Extensions;
using Junevy.Communication.Modbus.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;

namespace Junevy.Communication.Modbus.TCP
{
    public sealed class ModbusTCP : IModbus
    {
        private Socket? socket;
        private readonly ILogger<ModbusTCP> logger;
        private readonly IResponseParser responseParser;
        private readonly IModbusFrameBuilder frameBuilder;
        private readonly SemaphoreSlim requestLock = new(1, 1);
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private long lastTimestamp;
        private ushort transactionId;
        private bool disposed;

        public ModbusTCPConfig Config { get; private set; }
        public bool IsConnected => !disposed && IsSocketConnected(socket);
        public ModbusProtocolType ProtocolType => ModbusProtocolType.TCP;

        public ModbusTCP(ModbusTCPConfig config)
            : this(config, NullLogger<ModbusTCP>.Instance, new TcpProtocolParser())
        {
        }

        public ModbusTCP(ModbusTCPConfig config, ILogger<ModbusTCP> logger)
            : this(config, logger, new TcpProtocolParser())
        {
        }

        public ModbusTCP(ModbusTCPConfig config, ILogger<ModbusTCP> logger, IResponseParser responseParser)
            : this(config, logger, responseParser, new ModbusFrameBuilder())
        {
        }

        public ModbusTCP(
            ModbusTCPConfig config,
            ILogger<ModbusTCP> logger,
            IResponseParser responseParser,
            IModbusFrameBuilder frameBuilder)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            Config = config;
            this.logger = logger ?? NullLogger<ModbusTCP>.Instance;
            this.responseParser = responseParser ?? new TcpProtocolParser();
            this.frameBuilder = frameBuilder ?? new ModbusFrameBuilder();
            socket = CreateSocket();
        }

        public bool Connect()
        {
            ThrowIfDisposed();

            requestLock.Wait();
            try
            {
                return ConnectCore();
            }
            finally
            {
                requestLock.Release();
            }
        }

        public Task<bool> ConnectAsync()
        {
            return Task.Run(Connect);
        }

        public void Disconnect()
        {
            requestLock.Wait();
            try
            {
                DisconnectCore();
            }
            finally
            {
                requestLock.Release();
            }
        }

        private bool ConnectCore()
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
                    logger.LogWarning(" [Connect] Connection timed out: {Timeout}ms.", Config.ConnectTimeout);
                    return false;
                }

                socket.EndConnect(result);
                logger.LogDebug(" [Connect] Connected to {Address}:{Port}.", Config.Address, Config.Port);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, " [Connect] Connection failed.");
                socket?.Dispose();
                socket = null;
                return false;
            }
        }

        private void DisconnectCore()
        {
            try
            {
                if (socket?.Connected ?? false)
                    socket.Disconnect(false);

                socket?.Dispose();
                socket = null;
                logger.LogDebug(" [Disconnect] Disconnected from {Address}:{Port}.", Config.Address, Config.Port);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, " [Disconnect] Disconnect failed.");
            }
        }

        public ModbusResult<byte[]> Request(ModbusRequest request)
        {
            if (!ModbusHelper.CheckRequest(request))
            {
                logger.LogWarning(" [Request] Invalid request: {@Request}.", request);
                return ModbusResult<byte[]>.Fail(" [Request] Invalid request.", ModbusErrorKind.InvalidRequest);
            }

            requestLock.Wait();
            try
            {
                request.TransactionId = transactionId++;
                return ExecuteRequestWithRetry(request);
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                logger.LogError(ex, " [Request] Request failed.");
                MarkConnectionFaulted();
                return ModbusResult<byte[]>.Fail($" [Request] Request failed: {ex.Message}");
            }
            finally
            {
                requestLock.Release();
            }
        }

        public async Task<ModbusResult<byte[]>> RequestAsync(
            ModbusRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!ModbusHelper.CheckRequest(request))
            {
                logger.LogWarning(" [RequestAsync] Invalid request: {@Request}.", request);
                return ModbusResult<byte[]>.Fail(" [RequestAsync] Invalid request.", ModbusErrorKind.InvalidRequest);
            }

            var lockTaken = false;
            try
            {
                await requestLock.WaitAsync(cancellationToken);
                lockTaken = true;

                request.TransactionId = transactionId++;
                return await ExecuteRequestWithRetryAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning(" [RequestAsync] Request cancelled.");
                return ModbusResult<byte[]>.Fail(" [RequestAsync] Request cancelled.", ModbusErrorKind.Cancelled);
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                logger.LogError(ex, " [RequestAsync] Request failed.");
                MarkConnectionFaulted();
                return ModbusResult<byte[]>.Fail($" [RequestAsync] Request failed: {ex.Message}");
            }
            finally
            {
                if (lockTaken)
                    requestLock.Release();
            }
        }

        public bool CheckConnection() => IsConnected;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            socket?.Dispose();
            socket = null;
            requestLock.Dispose();
            logger.LogDebug(" [Dispose] ModbusTCP disposed.");
        }

        private bool Send(ModbusRequest request)
        {
            byte[]? frame = null;
            try
            {
                frame = ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxTcpAduLength);
                if (!frameBuilder.TryWriteRequestFrame(request, ProtocolType, frame, out int bytesWritten))
                    return false;

                int totalSent = 0;
                while (totalSent < bytesWritten)
                {
                    int sent = socket!.Send(frame, totalSent, bytesWritten - totalSent, SocketFlags.None);
                    totalSent += sent;
                    logger.LogDebug(" [Send] Total sent: {Total}, current: {Current}.", totalSent, sent);

                    if (sent == 0)
                        return false;
                }

                logger.Tx("ModbusTCP", new ArraySegment<byte>(frame, 0, bytesWritten).ToArray(), stopwatch, ref lastTimestamp);
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                logger.LogError(" [Send] Send timed out.");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, " [Send] Send failed.");
                throw;
            }
            finally
            {
                if (frame != null)
                    ArrayPool<byte>.Shared.Return(frame);
            }
        }

        private ModbusResult<byte[]> ExecuteRequestWithRetry(ModbusRequest request)
        {
            ModbusResult<byte[]> lastResult = ModbusResult<byte[]>.Fail("Request was not executed.");
            int attempts = GetAttemptCount();

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                if (!EnsureConnected())
                {
                    lastResult = ModbusResult<byte[]>.Fail(" [Request] Not connected.", ModbusErrorKind.ConnectionClosed);
                    if (attempt < attempts)
                    {
                        WaitBeforeRetry();
                        continue;
                    }

                    return lastResult;
                }

                logger.LogDebug(" [Request] Attempt {Attempt}/{Attempts}: {@Request}.", attempt, attempts, request);

                try
                {
                    if (!Send(request))
                    {
                        lastResult = ModbusResult<byte[]>.Fail(" [Request] Send failed.", ModbusErrorKind.ConnectionClosed);
                        MarkConnectionFaulted();
                    }
                    else
                    {
                        lastResult = Read(request);
                        if (lastResult.IsSuccess)
                            return lastResult;

                        // Modbus exception responses are terminal answers — return immediately, no resend.
                        if (!lastResult.IsSuccess && lastResult.ErrorKind == ModbusErrorKind.ModbusException)
                            return lastResult;

                        logger.LogWarning(" [Request] Attempt {Attempt}/{Attempts} failed: {Error}.", attempt, attempts, lastResult.ErrorMessage);
                        if (ShouldReconnectAfterFailure(lastResult))
                            MarkConnectionFaulted();
                    }
                }
                catch (Exception ex) when (IsCommunicationException(ex))
                {
                    logger.LogWarning(ex, " [Request] Attempt {Attempt}/{Attempts} failed.", attempt, attempts);
                    lastResult = ModbusResult<byte[]>.Fail($" [Request] {ex.Message}");
                    MarkConnectionFaulted();
                }

                if (attempt < attempts)
                    WaitBeforeRetry();
            }

            return lastResult;
        }

        private async Task<ModbusResult<byte[]>> ExecuteRequestWithRetryAsync(
            ModbusRequest request,
            CancellationToken cancellationToken)
        {
            ModbusResult<byte[]> lastResult = ModbusResult<byte[]>.Fail("Request was not executed.");
            int attempts = GetAttemptCount();

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!await EnsureConnectedAsync(cancellationToken))
                {
                    lastResult = ModbusResult<byte[]>.Fail(" [RequestAsync] Not connected.", ModbusErrorKind.ConnectionClosed);
                    if (attempt < attempts)
                    {
                        await WaitBeforeRetryAsync(cancellationToken);
                        continue;
                    }

                    return lastResult;
                }

                logger.LogDebug(" [RequestAsync] Attempt {Attempt}/{Attempts}: {@Request}.", attempt, attempts, request);

                try
                {
                    if (!await SendAsync(request, cancellationToken))
                    {
                        lastResult = ModbusResult<byte[]>.Fail(" [RequestAsync] Send failed.", ModbusErrorKind.ConnectionClosed);
                        MarkConnectionFaulted();
                    }
                    else
                    {
                        lastResult = await ReadAsync(request, cancellationToken);
                        if (lastResult.IsSuccess)
                            return lastResult;

                        // Modbus exception responses are terminal answers — return immediately, no resend.
                        if (!lastResult.IsSuccess && lastResult.ErrorKind == ModbusErrorKind.ModbusException)
                            return lastResult;

                        logger.LogWarning(" [RequestAsync] Attempt {Attempt}/{Attempts} failed: {Error}.", attempt, attempts, lastResult.ErrorMessage);
                        if (ShouldReconnectAfterFailure(lastResult))
                            MarkConnectionFaulted();
                    }
                }
                catch (Exception ex) when (IsCommunicationException(ex))
                {
                    logger.LogWarning(ex, " [RequestAsync] Attempt {Attempt}/{Attempts} failed.", attempt, attempts);
                    lastResult = ModbusResult<byte[]>.Fail($" [RequestAsync] {ex.Message}");
                    MarkConnectionFaulted();
                }

                if (attempt < attempts)
                    await WaitBeforeRetryAsync(cancellationToken);
            }

            return lastResult;
        }

        private async ValueTask<bool> SendAsync(ModbusRequest request, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Send(request);
            }, cancellationToken);
        }

        private ModbusResult<byte[]> Read(ModbusRequest request)
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
                    logger.LogError(" [Read] Invalid PDU length: {PduLength}.", pduLength);
                    return ModbusResult<byte[]>.Fail($" [Read] Invalid PDU length: {pduLength}.", ModbusErrorKind.ProtocolViolation);
                }

                int totalLength = 6 + pduLength;
                var payloadResult = ReceiveExact(frame, 6, pduLength);
                if (!payloadResult.IsSuccess)
                    return payloadResult;

                var data = new ReadOnlyMemory<byte>(frame, 0, totalLength);
                logger.Rx("ModbusTCP", data.Span, stopwatch, ref lastTimestamp);

                var parsed = responseParser.ParseResponse(data, request);
                return parsed.IsSuccess
                    ? ModbusResult<byte[]>.Success(parsed.Data.ToArray())
                    : ModbusResult<byte[]>.Fail(parsed.ErrorMessage ?? " [Read] Parse error.", parsed.ErrorKind, data.ToArray());
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                logger.LogError(" [Read] Read timed out.");
                return ModbusResult<byte[]>.Fail(" [Read] Read timeout.", ModbusErrorKind.Timeout);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, " [Read] Read failed.");
                throw;
            }
            finally
            {
                if (frame != null)
                    ArrayPool<byte>.Shared.Return(frame);
            }
        }

        private async ValueTask<ModbusResult<byte[]>> ReadAsync(
            ModbusRequest request,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Read(request);
            }, cancellationToken);
        }

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
            socket?.Dispose();
            socket = CreateSocket();
            socket.ReceiveTimeout = Config.ReadTimeOut;
            socket.SendTimeout = Config.WriteTimeOut;
            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }

        private static Socket CreateSocket()
            => new Socket(AddressFamily.InterNetwork, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);

        private bool EnsureConnected()
        {
            if (IsConnected)
                return true;

            if (!Config.Reconnect)
                return false;

            logger.LogInformation(" [Reconnect] TCP connection is not available. Reconnecting to {Address}:{Port}.", Config.Address, Config.Port);
            try
            {
                return ConnectCore();               // 已在请求锁内，走无锁核心，避免重入死锁
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                logger.LogWarning(ex, " [Reconnect] TCP reconnect failed.");
                return false;
            }
        }

        private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (IsConnected)
                return true;

            if (!Config.Reconnect)
                return false;

            logger.LogInformation(" [ReconnectAsync] TCP connection is not available. Reconnecting to {Address}:{Port}.", Config.Address, Config.Port);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await Task.Run(ConnectCore); // 已在请求锁内，走无锁核心，避免重入死锁
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                logger.LogWarning(ex, " [ReconnectAsync] TCP reconnect failed.");
                return false;
            }
        }

        private void MarkConnectionFaulted()
        {
            try
            {
                socket?.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, " [MarkConnectionFaulted] Error disposing socket.");
            }
            finally
            {
                socket = null;
            }
        }

        private int GetAttemptCount()
            => Math.Max(1, Config.RetryCount + 1);

        private void WaitBeforeRetry()
        {
            if (Config.RetryInterval > 0)
                Thread.Sleep(Config.RetryInterval);
        }

        private Task WaitBeforeRetryAsync(CancellationToken cancellationToken)
        {
            return Config.RetryInterval > 0
                ? Task.Delay(Config.RetryInterval, cancellationToken)
                : Task.CompletedTask;
        }

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

        private static bool IsCommunicationException(Exception ex)
        {
            return ex is SocketException
                || ex is IOException
                || ex is ObjectDisposedException
                || ex is InvalidOperationException
                || ex is EndOfStreamException;
        }

        private static bool ShouldReconnectAfterFailure(ModbusResult<byte[]> result)
            => result.ErrorKind is ModbusErrorKind.Timeout or ModbusErrorKind.ConnectionClosed;

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(ModbusTCP));
        }
    }
}
