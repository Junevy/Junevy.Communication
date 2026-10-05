using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Core.Parsing;
using Junevy.Communication.Modbus.Extensions;
using Junevy.Communication.Modbus.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.IO.Ports;

namespace Junevy.Communication.Modbus.RTU
{
    public sealed class ModbusRTU : IModbus
    {
        private bool disposed;
        private readonly ILogger<ModbusRTU> logger;
        private readonly IResponseParser responseParser;
        private readonly IModbusFrameBuilder frameBuilder;
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private long lastTimestamp;

        public bool IsConnected => !disposed && serialPort.IsOpen;
        public ModbusProtocolType ProtocolType => ModbusProtocolType.RTU;
        private readonly SerialPort serialPort = new();
        private readonly SemaphoreSlim requestLock = new(1, 1);

        /// <summary>
        /// Modbus RTU configuration.
        /// </summary>
        public ModbusRTUConfig Config { get; }

        public ModbusRTU(ModbusRTUConfig config)
            : this(config, NullLogger<ModbusRTU>.Instance, new RtuProtocolParser())
        {
        }

        public ModbusRTU(ModbusRTUConfig config, ILogger<ModbusRTU> logger)
            : this(config, logger, new RtuProtocolParser())
        {
        }

        public ModbusRTU(ModbusRTUConfig config, ILogger<ModbusRTU> logger, IResponseParser responseParser)
            : this(config, logger, responseParser, new ModbusFrameBuilder())
        {
        }

        public ModbusRTU(
            ModbusRTUConfig config,
            ILogger<ModbusRTU> logger,
            IResponseParser responseParser,
            IModbusFrameBuilder frameBuilder)
        {
            this.Config = config
                ?? throw new ArgumentNullException(nameof(config), nameof(config) + " is null!");
            this.logger = logger ?? NullLogger<ModbusRTU>.Instance;
            this.responseParser = responseParser ?? new RtuProtocolParser();
            this.frameBuilder = frameBuilder ?? new ModbusFrameBuilder();
        }

        /// <summary>
        /// Opens the serial port connection.
        /// </summary>
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

        /// <summary>
        /// Asynchronously opens the serial port connection.
        /// </summary>
        public Task<bool> ConnectAsync()
        {
            // SerialPort does not have an async Open method, so we use Task.Run.
            return Task.Run(Connect);
        }

        private bool ConnectCore()
        {
            if (serialPort.IsOpen)
            {
                serialPort.Close();
            }

            InitialConnection();

            try
            {
                serialPort.Open();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, " [Connect] Failed to open port {PortName}.", Config.PortName);
                return false;
            }
            return true;
        }

        private void InitialConnection()
        {
            if (IsConnected) return;

            try
            {
                serialPort.PortName = Config.PortName;
                serialPort.BaudRate = Config.BaudRate;
                serialPort.Parity = Config.Parity;
                serialPort.DataBits = Config.DataBits;
                serialPort.StopBits = Config.StopBits;
                serialPort.DtrEnable = Config.DtrEnable;
                serialPort.RtsEnable = Config.RtsEnable;

                serialPort.ReadTimeout = Config.ReadTimeOut;
                serialPort.WriteTimeout = Config.WriteTimeOut;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, " [InitialConnection] Configure port failed: {@Config}.", Config);
                throw;
            }
        }

        /// <summary>
        /// Closes the serial port connection. The instance can be reconnected afterwards.
        /// </summary>
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

        private void DisconnectCore()
        {
            try
            {
                if (serialPort.IsOpen)
                {
                    serialPort.Close();
                    logger.LogDebug(" [Disconnect] Port {PortName} closed.", Config.PortName);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, " [Disconnect] Failed to close port {PortName}.", Config.PortName);
            }
        }

        public ModbusResult<byte[]> Request(ModbusRequest request)
        {
            logger.LogInformation(" [Request] Executing request: {@Request}", request);

            if (!ModbusHelper.CheckRequest(request))
                return ModbusResult<byte[]>.Fail(" [Request] Invalid request.", ModbusErrorKind.InvalidRequest, request.Data);

            requestLock.Wait();
            try
            {
                return ExecuteRequestWithRetry(request);
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                logger.LogError(ex, " [Request] Request execution failed.");
                MarkConnectionFaulted();
                return ModbusResult<byte[]>.Fail($" [Request] Request failed: {ex.Message}");
            }
            finally
            {
                requestLock.Release();
            }
        }

        private bool Send(ModbusRequest request)
        {
            ThrowIfDisposed();

            try
            {
                var requestFrame = System.Buffers.ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxRtuAduLength);
                try
                {
                    if (!frameBuilder.TryWriteRequestFrame(request, ProtocolType, requestFrame, out int bytesWritten))
                        return false;

                    serialPort.DiscardInBuffer();
                    serialPort.DiscardOutBuffer();

                    serialPort.Write(requestFrame, 0, bytesWritten);
                    logger.Tx("ModbusRTU", new ArraySegment<byte>(requestFrame, 0, bytesWritten).ToArray(), stopwatch, ref lastTimestamp);
                    return true;
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(requestFrame);
                }
            }
            catch (TimeoutException)
            {
                logger.LogError(" [Send] Write timeout: {Timeout}ms.", Config.WriteTimeOut);
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, " [Send] Send failed.");
                throw;
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
                    lastResult = ModbusResult<byte[]>.Fail(" [Request] Port not open.", ModbusErrorKind.ConnectionClosed);
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
                        lastResult = ModbusResult<byte[]>.Fail(" [Request] Send frame failed.", ModbusErrorKind.ConnectionClosed);
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

        private ModbusResult<byte[]> Read(ModbusRequest request)
        {
            var pool = System.Buffers.ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxRtuAduLength + 1);
            int readCounts = 0;

            try
            {
                while (true)
                {
                    int readBytes = 0;
                    try
                    {
                        readBytes = serialPort.Read(pool, readCounts, pool.Length - readCounts);
                        readCounts += readBytes;
                    }
                    catch (TimeoutException)
                    {
                        logger.LogError(" [Read] Read timeout: {Timeout}ms.", Config.ReadTimeOut);
                        return ModbusResult<byte[]>.Fail($" [Read] Read slave timeout: ({Config.ReadTimeOut}ms).", ModbusErrorKind.Timeout);
                    }

                    logger.LogDebug(" [Read] Bytes received: {Count}.", readCounts);

                    if (readCounts < 5) continue;
                    if (readCounts >= pool.Length)
                        return ModbusResult<byte[]>.Fail(" [Read] Receive buffer is full before a valid RTU frame was parsed.");
                    var memory = pool.AsMemory(0, readCounts);

                    var parseResult = responseParser.ParseResponse(memory, request);

                    if (parseResult.IsSuccess)
                    {
                        if (parseResult.Data.Length <= 0)
                        {
                            logger.LogWarning(" [Read] Parsed frame has zero length.");
                            return ModbusResult<byte[]>.Fail(" [Read] Parsed frame has zero length.", ModbusErrorKind.ProtocolViolation);
                        }
                        logger.Rx("ModbusRTU", parseResult.Data.Span, stopwatch, ref lastTimestamp);
                        return ModbusResult<byte[]>.Success(parseResult.Data.ToArray());
                    }

                    logger.Rx("ModbusRTU", parseResult.Data.Span, stopwatch, ref lastTimestamp);

                    // Exception responses are authoritative answers, not resync noise — surface them immediately.
                    if (parseResult.ErrorKind == ModbusErrorKind.ModbusException)
                        return ModbusResult<byte[]>.Fail(parseResult.ErrorMessage!, parseResult.ErrorKind, parseResult.Data.ToArray());

                    logger.LogDebug(" [Read] Waiting {Interval}ms for next frame...", Config.IntervalTime);
                    Thread.Sleep(Config.IntervalTime);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, " [Read] Receive response failed.");
                throw;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pool);
            }
        }

        private async Task<ModbusResult<byte[]>> ExecuteRequestWithRetryAsync(
            ModbusRequest request,
            CancellationToken token)
        {
            ModbusResult<byte[]> lastResult = ModbusResult<byte[]>.Fail("Request was not executed.");
            int attempts = GetAttemptCount();

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                token.ThrowIfCancellationRequested();

                if (!await EnsureConnectedAsync(token))
                {
                    lastResult = ModbusResult<byte[]>.Fail(" [RequestAsync] Port not open.", ModbusErrorKind.ConnectionClosed);
                    if (attempt < attempts)
                    {
                        await WaitBeforeRetryAsync(token);
                        continue;
                    }

                    return lastResult;
                }

                logger.LogDebug(" [RequestAsync] Attempt {Attempt}/{Attempts}: {@Request}.", attempt, attempts, request);

                try
                {
                    if (!await SendAsync(request, token))
                    {
                        lastResult = ModbusResult<byte[]>.Fail(" [RequestAsync] Send frame failed.", ModbusErrorKind.ConnectionClosed);
                        MarkConnectionFaulted();
                    }
                    else
                    {
                        lastResult = await ReadAsync(request, token);
                        if (lastResult.IsSuccess)
                            return lastResult;

                        // Modbus exception responses are terminal answers — return immediately, no resend.
                        if (!lastResult.IsSuccess && lastResult.ErrorKind == ModbusErrorKind.ModbusException)
                            return lastResult;

                        logger.LogWarning(" [RequestAsync] Attempt {Attempt}/{Attempts} failed: {Error}.", attempt, attempts, lastResult.ErrorMessage);
                    }
                }
                catch (Exception ex) when (IsCommunicationException(ex))
                {
                    logger.LogWarning(ex, " [RequestAsync] Attempt {Attempt}/{Attempts} failed.", attempt, attempts);
                    lastResult = ModbusResult<byte[]>.Fail($" [RequestAsync] {ex.Message}");
                    MarkConnectionFaulted();
                }

                if (attempt < attempts)
                    await WaitBeforeRetryAsync(token);
            }

            return lastResult;
        }

        public async Task<ModbusResult<byte[]>> RequestAsync(ModbusRequest request, CancellationToken token = default)
        {
            logger.LogInformation(" [RequestAsync] Executing request: {@Request}", request);

            if (!ModbusHelper.CheckRequest(request))
                return ModbusResult<byte[]>.Fail(" [RequestAsync] Invalid request.", ModbusErrorKind.InvalidRequest, request.Data);

            var lockTaken = false;
            try
            {
                await requestLock.WaitAsync(token);
                lockTaken = true;
                return await ExecuteRequestWithRetryAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                logger.LogWarning(ex, " [RequestAsync] Request cancelled.");
                return ModbusResult<byte[]>.Fail(" [RequestAsync] Request cancelled.", ModbusErrorKind.Cancelled);
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                logger.LogError(ex, " [RequestAsync] Request execution failed.");
                MarkConnectionFaulted();
                return ModbusResult<byte[]>.Fail($" [RequestAsync] Request failed: {ex.Message}");
            }
            finally
            {
                if (lockTaken)
                    requestLock.Release();
            }
        }

        private async Task<bool> SendAsync(ModbusRequest request, CancellationToken token = default)
        {
            ThrowIfDisposed();

            try
            {
                int frameLength = frameBuilder.GetRequestFrameLength(request, ProtocolType);
                byte[] requestFrame = System.Buffers.ArrayPool<byte>.Shared.Rent(frameLength);
                token.ThrowIfCancellationRequested();

                try
                {
                    if (!frameBuilder.TryWriteRequestFrame(request, ProtocolType, requestFrame, out int bytesWritten))
                        return false;

                    serialPort.DiscardInBuffer();
                    serialPort.DiscardOutBuffer();

                    await serialPort.BaseStream.WriteAsync(requestFrame, 0, bytesWritten, token);
                    logger.Tx("ModbusRTU", new ArraySegment<byte>(requestFrame, 0, bytesWritten).ToArray(), stopwatch, ref lastTimestamp);
                    return true;
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(requestFrame);
                }
            }
            catch (TimeoutException)
            {
                logger.LogError(" [SendAsync] Write timeout: {Timeout}ms.", Config.WriteTimeOut);
                return false;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning(" [SendAsync] Send cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, " [SendAsync] Send failed.");
                throw;
            }
        }

        private async Task<ModbusResult<byte[]>> ReadAsync(ModbusRequest request, CancellationToken token = default)
        {
            var pool = System.Buffers.ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxRtuAduLength + 1);
            int readCounts = 0;

            try
            {
                var readTimeoutToken = CancellationTokenSource.CreateLinkedTokenSource(token);
                readTimeoutToken.CancelAfter(Config.ReadTimeOut);
                while (true)
                {
                    readTimeoutToken.Token.ThrowIfCancellationRequested();
                    int readBytes = 0;
                    try
                    {
                        readBytes = await Task.Run(() => serialPort.Read(pool, readCounts, pool.Length - readCounts), readTimeoutToken.Token);
                        readCounts += readBytes;
                    }
                    catch (TimeoutException)
                    {
                        logger.LogError(" [ReadAsync] Read timeout: {Timeout}ms.", Config.ReadTimeOut);
                        return ModbusResult<byte[]>.Fail($" [ReadAsync] Read slave timeout: ({Config.ReadTimeOut}ms).", ModbusErrorKind.Timeout);
                    }

                    if (readCounts < 5) continue;
                    if (readCounts >= pool.Length)
                        return ModbusResult<byte[]>.Fail(" [ReadAsync] Receive buffer is full before a valid RTU frame was parsed.");
                    var memory = pool.AsMemory(0, readCounts);

                    var parseResult = responseParser.ParseResponse(memory, request);
                    if (parseResult.IsSuccess)
                    {
                        if (parseResult.Data.Length <= 0)
                        {
                            logger.LogWarning(" [ReadAsync] Parsed frame has zero length.");
                            return ModbusResult<byte[]>.Fail(" [ReadAsync] Parsed frame has zero length.", ModbusErrorKind.ProtocolViolation);
                        }

                        logger.Rx("ModbusRTU", parseResult.Data.Span, stopwatch, ref lastTimestamp);
                        return ModbusResult<byte[]>.Success(parseResult.Data.Span.ToArray());
                    }

                    logger.Rx("ModbusRTU", parseResult.Data.Span, stopwatch, ref lastTimestamp);

                    // Exception responses are authoritative answers, not resync noise — surface them immediately.
                    if (parseResult.ErrorKind == ModbusErrorKind.ModbusException)
                        return ModbusResult<byte[]>.Fail(parseResult.ErrorMessage!, parseResult.ErrorKind, parseResult.Data.ToArray());

                    logger.LogDebug(" [ReadAsync] Waiting {Interval}ms for next frame...", Config.IntervalTime);
                    await Task.Delay(Config.IntervalTime, token);
                }
            }
            catch (OperationCanceledException ex)
            {
                logger.LogError(ex, " [ReadAsync] Read cancelled.");
                return ModbusResult<byte[]>.Fail(ex.ToString());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, " [ReadAsync] Receive response failed.");
                throw;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pool);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            try
            {
                if (serialPort.IsOpen)
                    serialPort.Close();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, " [Dispose] Error closing serial port.");
            }

            serialPort.Dispose();
            requestLock.Dispose();
            logger.LogDebug(" [Dispose] ModbusRTU disposed.");
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(ModbusRTU));
        }

        private bool EnsureConnected()
        {
            if (IsConnected)
                return true;

            if (!Config.Reconnect)
                return false;

            logger.LogInformation(" [Reconnect] Serial port {PortName} is not open. Reconnecting.", Config.PortName);
            try
            {
                return ConnectCore();               // 已在请求锁内，走无锁核心，避免重入死锁
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                logger.LogWarning(ex, " [Reconnect] Serial reconnect failed.");
                return false;
            }
        }

        private async Task<bool> EnsureConnectedAsync(CancellationToken token)
        {
            if (IsConnected)
                return true;

            if (!Config.Reconnect)
                return false;

            logger.LogInformation(" [ReconnectAsync] Serial port {PortName} is not open. Reconnecting.", Config.PortName);
            try
            {
                token.ThrowIfCancellationRequested();
                return await Task.Run(ConnectCore); // 已在请求锁内，走无锁核心，避免重入死锁
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                logger.LogWarning(ex, " [ReconnectAsync] Serial reconnect failed.");
                return false;
            }
        }

        private void MarkConnectionFaulted()
        {
            try
            {
                if (serialPort.IsOpen)
                    serialPort.Close();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, " [MarkConnectionFaulted] Error closing serial port.");
            }
        }

        private int GetAttemptCount()
            => Math.Max(1, Config.RetryCount + 1);

        private void WaitBeforeRetry()
        {
            if (Config.RetryInterval > 0)
                Thread.Sleep(Config.RetryInterval);
        }

        private Task WaitBeforeRetryAsync(CancellationToken token)
        {
            return Config.RetryInterval > 0
                ? Task.Delay(Config.RetryInterval, token)
                : Task.CompletedTask;
        }

        private static bool IsCommunicationException(Exception ex)
        {
            return ex is TimeoutException
                || ex is IOException
                || ex is InvalidOperationException
                || ex is UnauthorizedAccessException
                || ex is ObjectDisposedException;
        }
    }
}
