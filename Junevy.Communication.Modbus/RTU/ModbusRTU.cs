using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Core.Parsing;
using Junevy.Communication.Modbus.Core.Transports;
using Junevy.Communication.Modbus.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Ports;

namespace Junevy.Communication.Modbus.RTU
{
    /// <summary>
    /// Modbus RTU 客户端。请求/重试/重连骨架由 <see cref="ModbusTransportBase"/> 提供，
    /// 本类只实现串口相关的连接与收发（DiscardInBuffer/OutBuffer、Read-until-frame 循环、CRC 由解析器处理）。
    /// </summary>
    public sealed class ModbusRTU : ModbusTransportBase
    {
        private readonly SerialPort serialPort = new();

        /// <summary>
        /// Modbus RTU configuration.
        /// </summary>
        public ModbusRTUConfig Config { get; }

        public override bool IsConnected => !disposed && serialPort.IsOpen;
        public override ModbusProtocolType ProtocolType => ModbusProtocolType.RTU;

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
            : base(logger, responseParser, frameBuilder)
        {
            Config = config
                ?? throw new ArgumentNullException(nameof(config), nameof(config) + " is null!");
        }

        // ————————————————— 连接钩子 —————————————————

        protected override bool OpenConnection()
        {
            if (serialPort.IsOpen)
            {
                serialPort.Close();
            }

            ConfigurePort();

            try
            {
                serialPort.Open();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, " [Connect] Failed to open port {PortName}.", Config.PortName);
                return false;
            }
            return true;
        }

        protected override async Task<bool> OpenConnectionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(OpenConnection, cancellationToken);
        }

        private void ConfigurePort()
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
                Logger.LogError(ex, " [InitialConnection] Configure port failed: {@Config}.", Config);
                throw;
            }
        }

        protected override void CloseConnection()
        {
            try
            {
                if (serialPort.IsOpen)
                {
                    serialPort.Close();
                    Logger.LogDebug(" [Disconnect] Port {PortName} closed.", Config.PortName);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, " [Disconnect] Failed to close port {PortName}.", Config.PortName);
            }
        }

        protected override void InvalidateConnection()
        {
            try
            {
                if (serialPort.IsOpen)
                    serialPort.Close();
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, " [MarkConnectionFaulted] Error closing serial port.");
            }
        }

        protected override void DisposeConnection()
        {
            try
            {
                if (serialPort.IsOpen)
                    serialPort.Close();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, " [Dispose] Error closing serial port.");
            }

            serialPort.Dispose();
        }

        // ————————————————— 配置转发 —————————————————

        protected override bool ReconnectEnabled => Config.Reconnect;
        protected override int RetryCount => Config.RetryCount;
        protected override int RetryInterval => Config.RetryInterval;
        protected override bool AssignsTransactionId => false;

        // ————————————————— 消息/日志文本钩子 —————————————————

        protected override string GetNotConnectedMessage(bool isAsync)
            => isAsync ? " [RequestAsync] Port not open." : " [Request] Port not open.";

        protected override string GetSendFailedMessage(bool isAsync)
            => isAsync ? " [RequestAsync] Send frame failed." : " [Request] Send frame failed.";

        protected override string GetRequestFailedLogText(bool isAsync)
            => isAsync ? " [RequestAsync] Request execution failed." : " [Request] Request execution failed.";

        protected override void LogReconnectAttempt(bool isAsync)
        {
            if (isAsync)
                Logger.LogInformation(" [ReconnectAsync] Serial port {PortName} is not open. Reconnecting.", Config.PortName);
            else
                Logger.LogInformation(" [Reconnect] Serial port {PortName} is not open. Reconnecting.", Config.PortName);
        }

        protected override void LogReconnectFailed(Exception ex, bool isAsync)
        {
            if (isAsync)
                Logger.LogWarning(ex, " [ReconnectAsync] Serial reconnect failed.");
            else
                Logger.LogWarning(ex, " [Reconnect] Serial reconnect failed.");
        }

        protected override void LogRequestStarted(ModbusRequest request, bool isAsync)
        {
            if (isAsync)
                Logger.LogInformation(" [RequestAsync] Executing request: {@Request}", request);
            else
                Logger.LogInformation(" [Request] Executing request: {@Request}", request);
        }

        protected override ModbusResult<byte[]> CreateInvalidRequestResult(ModbusRequest request, bool isAsync)
        {
            return isAsync
                ? ModbusResult<byte[]>.Fail(" [RequestAsync] Invalid request.", ModbusErrorKind.InvalidRequest, request.Data)
                : ModbusResult<byte[]>.Fail(" [Request] Invalid request.", ModbusErrorKind.InvalidRequest, request.Data);
        }

        // ————————————————— 收发钩子 —————————————————

        protected override bool SendFrame(ModbusRequest request)
        {
            ThrowIfDisposed();

            try
            {
                var requestFrame = System.Buffers.ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxRtuAduLength);
                try
                {
                    if (!FrameBuilder.TryWriteRequestFrame(request, ProtocolType, requestFrame, out int bytesWritten))
                        return false;

                    serialPort.DiscardInBuffer();
                    serialPort.DiscardOutBuffer();

                    serialPort.Write(requestFrame, 0, bytesWritten);
                    LogTx("ModbusRTU", new ArraySegment<byte>(requestFrame, 0, bytesWritten).ToArray());
                    return true;
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(requestFrame);
                }
            }
            catch (TimeoutException)
            {
                Logger.LogError(" [Send] Write timeout: {Timeout}ms.", Config.WriteTimeOut);
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, " [Send] Send failed.");
                throw;
            }
        }

        protected override async Task<bool> SendFrameAsync(ModbusRequest request, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            try
            {
                int frameLength = FrameBuilder.GetRequestFrameLength(request, ProtocolType);
                byte[] requestFrame = System.Buffers.ArrayPool<byte>.Shared.Rent(frameLength);
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (!FrameBuilder.TryWriteRequestFrame(request, ProtocolType, requestFrame, out int bytesWritten))
                        return false;

                    serialPort.DiscardInBuffer();
                    serialPort.DiscardOutBuffer();

                    await serialPort.BaseStream.WriteAsync(requestFrame, 0, bytesWritten, cancellationToken);
                    LogTx("ModbusRTU", new ArraySegment<byte>(requestFrame, 0, bytesWritten).ToArray());
                    return true;
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(requestFrame);
                }
            }
            catch (TimeoutException)
            {
                Logger.LogError(" [SendAsync] Write timeout: {Timeout}ms.", Config.WriteTimeOut);
                return false;
            }
            catch (OperationCanceledException)
            {
                Logger.LogWarning(" [SendAsync] Send cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, " [SendAsync] Send failed.");
                throw;
            }
        }

        protected override ModbusResult<byte[]> ReceiveFrame(ModbusRequest request)
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
                        Logger.LogError(" [Read] Read timeout: {Timeout}ms.", Config.ReadTimeOut);
                        return ModbusResult<byte[]>.Fail($" [Read] Read slave timeout: ({Config.ReadTimeOut}ms).", ModbusErrorKind.Timeout);
                    }

                    Logger.LogDebug(" [Read] Bytes received: {Count}.", readCounts);

                    if (readCounts < 5) continue;
                    if (readCounts >= pool.Length)
                        return ModbusResult<byte[]>.Fail(" [Read] Receive buffer is full before a valid RTU frame was parsed.");
                    var memory = pool.AsMemory(0, readCounts);

                    var parseResult = ResponseParser.ParseResponse(memory, request);

                    if (parseResult.IsSuccess)
                    {
                        if (parseResult.Data.Length <= 0)
                        {
                            Logger.LogWarning(" [Read] Parsed frame has zero length.");
                            return ModbusResult<byte[]>.Fail(" [Read] Parsed frame has zero length.", ModbusErrorKind.ProtocolViolation);
                        }
                        LogRx("ModbusRTU", parseResult.Data.Span);
                        return ModbusResult<byte[]>.Success(parseResult.Data.ToArray());
                    }

                    LogRx("ModbusRTU", parseResult.Data.Span);

                    // Exception responses are authoritative answers, not resync noise — surface them immediately.
                    if (parseResult.ErrorKind == ModbusErrorKind.ModbusException)
                        return ModbusResult<byte[]>.Fail(parseResult.ErrorMessage!, parseResult.ErrorKind, parseResult.Data.ToArray());

                    Logger.LogDebug(" [Read] Waiting {Interval}ms for next frame...", Config.IntervalTime);
                    Thread.Sleep(Config.IntervalTime);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, " [Read] Receive response failed.");
                throw;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pool);
            }
        }

        protected override async Task<ModbusResult<byte[]>> ReceiveFrameAsync(ModbusRequest request, CancellationToken cancellationToken)
        {
            var pool = System.Buffers.ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxRtuAduLength + 1);
            int readCounts = 0;

            try
            {
                var readTimeoutToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
                        Logger.LogError(" [ReadAsync] Read timeout: {Timeout}ms.", Config.ReadTimeOut);
                        return ModbusResult<byte[]>.Fail($" [ReadAsync] Read slave timeout: ({Config.ReadTimeOut}ms).", ModbusErrorKind.Timeout);
                    }

                    if (readCounts < 5) continue;
                    if (readCounts >= pool.Length)
                        return ModbusResult<byte[]>.Fail(" [ReadAsync] Receive buffer is full before a valid RTU frame was parsed.");
                    var memory = pool.AsMemory(0, readCounts);

                    var parseResult = ResponseParser.ParseResponse(memory, request);
                    if (parseResult.IsSuccess)
                    {
                        if (parseResult.Data.Length <= 0)
                        {
                            Logger.LogWarning(" [ReadAsync] Parsed frame has zero length.");
                            return ModbusResult<byte[]>.Fail(" [ReadAsync] Parsed frame has zero length.", ModbusErrorKind.ProtocolViolation);
                        }

                        LogRx("ModbusRTU", parseResult.Data.Span);
                        return ModbusResult<byte[]>.Success(parseResult.Data.Span.ToArray());
                    }

                    LogRx("ModbusRTU", parseResult.Data.Span);

                    // Exception responses are authoritative answers, not resync noise — surface them immediately.
                    if (parseResult.ErrorKind == ModbusErrorKind.ModbusException)
                        return ModbusResult<byte[]>.Fail(parseResult.ErrorMessage!, parseResult.ErrorKind, parseResult.Data.ToArray());

                    Logger.LogDebug(" [ReadAsync] Waiting {Interval}ms for next frame...", Config.IntervalTime);
                    await Task.Delay(Config.IntervalTime, cancellationToken);
                }
            }
            catch (OperationCanceledException ex)
            {
                Logger.LogError(ex, " [ReadAsync] Read cancelled.");
                return ModbusResult<byte[]>.Fail(ex.ToString());
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, " [ReadAsync] Receive response failed.");
                throw;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pool);
            }
        }
    }
}
