using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Extensions;
using Junevy.Communication.Modbus.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Net.Sockets;

namespace Junevy.Communication.Modbus.Core.Transports
{
    /// <summary>
    /// TCP/RTU 传输层公共基类：实现 <see cref="IModbus"/> 公开 API 与
    /// 请求/重试/重连骨架 —— 将原 <c>ModbusTcpClient</c>/<c>ModbusRtuClient</c> 中各持一份、
    /// 合计四份近似的重试循环（ExecuteRequestWithRetry(Async) ×2、EnsureConnected(Async) ×2、
    /// WaitBeforeRetry(Async) ×2、GetAttemptCount、IsCommunicationException、ThrowIfDisposed、
    /// requestLock/stopwatch/lastTimestamp/transactionId/disposed 字段）合并为基类中的
    /// 一份同步 + 一份异步实现。子类只需实现协议相关的连接/收发钩子。
    /// </summary>
    public abstract class ModbusTransportBase : IModbus
    {
        // ————— 原 TCP/RTU 各持一份的重复状态，上移到基类 —————
        private readonly SemaphoreSlim requestLock = new(1, 1);
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private long lastTimestamp;
        private ushort transactionId;

        /// <summary>由基类 Dispose 模板置位；子类 <see cref="IsConnected"/> 与 Dispose 依赖它。</summary>
        protected bool disposed;
        protected ILogger Logger { get; }
        protected IResponseParser ResponseParser { get; }
        protected IModbusFrameBuilder FrameBuilder { get; }

        protected ModbusTransportBase(
            ILogger logger,
            IResponseParser responseParser,
            IModbusFrameBuilder frameBuilder)
        {
            Logger = logger ?? NullLogger.Instance;
            ResponseParser = responseParser ?? throw new ArgumentNullException(nameof(responseParser));
            FrameBuilder = frameBuilder ?? throw new ArgumentNullException(nameof(frameBuilder));
        }

        // ————————————————— 协议钩子（子类实现） —————————————————

        public abstract ModbusProtocolType ProtocolType { get; }

        public abstract bool IsConnected { get; }

        /// <summary>无锁连接核心（TCP：地址校验 + ResetSocket + 连接；RTU：ConfigurePort + Open）。</summary>
        protected abstract bool OpenConnection();

        /// <summary>无锁异步连接核心（调用方持有 requestLock；连接超时由实现内部保证）。</summary>
        protected abstract Task<bool> OpenConnectionAsync(CancellationToken cancellationToken);

        /// <summary>Disconnect 核心（无锁，调用方持有 requestLock）。</summary>
        protected abstract void CloseConnection();

        /// <summary>连接失效处理（原 MarkConnectionFaulted：TCP 销毁 socket / RTU 关闭串口）。</summary>
        protected abstract void InvalidateConnection();

        /// <summary>发送请求帧（含协议特定封装：TCP MBAP 头 / RTU DiscardInBuffer + CRC）。</summary>
        protected abstract bool SendFrame(ModbusRequest request);

        protected abstract Task<bool> SendFrameAsync(ModbusRequest request, CancellationToken cancellationToken);

        /// <summary>接收并解析响应帧（TCP：6 字节头 + payload；RTU：Read-until-frame 循环）。</summary>
        protected abstract ModbusResult<byte[]> ReceiveFrame(ModbusRequest request);

        protected abstract Task<ModbusResult<byte[]>> ReceiveFrameAsync(ModbusRequest request, CancellationToken cancellationToken);

        /// <summary>释放底层连接资源（TCP：销毁 socket；RTU：关闭并 Dispose 串口）。</summary>
        protected abstract void DisposeConnection();

        /// <summary>是否允许失败后自动重连（转发自各自 Config.Reconnect）。</summary>
        protected abstract bool ReconnectEnabled { get; }

        /// <summary>首次失败后的重试次数（转发自各自 Config.RetryCount）。</summary>
        protected abstract int RetryCount { get; }

        /// <summary>重试间隔毫秒数（转发自各自 Config.RetryInterval）。</summary>
        protected abstract int RetryInterval { get; }

        /// <summary>
        /// TCP 有事务 ID 概念：每个逻辑请求在锁内分配自增 TID（request.TransactionId = transactionId++）。
        /// RTU 无事务 ID，保持现状不分配，返回 false。
        /// </summary>
        protected abstract bool AssignsTransactionId { get; }

        // ————————————————— 消息/日志文本钩子（子类提供，保持逐字不变） —————————————————

        /// <summary>EnsureConnected 失败时的结果消息。TCP：" [Request] Not connected."；RTU：" [Request] Port not open."。</summary>
        protected abstract string GetNotConnectedMessage(bool isAsync);

        /// <summary>发送失败时的结果消息。TCP：" [Request] Send failed."；RTU：" [Request] Send frame failed."。</summary>
        protected abstract string GetSendFailedMessage(bool isAsync);

        /// <summary>请求执行失败（请求锁内）时的日志文本。TCP：" [Request] Request failed."；RTU：" [Request] Request execution failed."。</summary>
        protected abstract string GetRequestFailedLogText(bool isAsync);

        /// <summary>重连尝试日志。TCP：" [Reconnect] TCP connection is not available…"；RTU：" [Reconnect] Serial port … Reconnecting."。</summary>
        protected abstract void LogReconnectAttempt(bool isAsync);

        /// <summary>重连失败日志。TCP：" [Reconnect] TCP reconnect failed."；RTU：" [Reconnect] Serial reconnect failed."。</summary>
        protected abstract void LogReconnectFailed(Exception ex, bool isAsync);

        /// <summary>请求开始日志（RTU 记录 " [Request] Executing request…"；TCP 默认不记录）。</summary>
        protected virtual void LogRequestStarted(ModbusRequest request, bool isAsync)
        {
        }

        /// <summary>
        /// 请求校验失败的结果。基类默认返回 Fail(" [Request] Invalid request.", InvalidRequest)；
        /// TCP 额外记录告警日志，RTU 额外携带 request.Data —— 由子类覆写以保持现状。
        /// </summary>
        protected virtual ModbusResult<byte[]> CreateInvalidRequestResult(ModbusRequest request, bool isAsync)
            => ModbusResult<byte[]>.Fail(
                isAsync ? " [RequestAsync] Invalid request." : " [Request] Invalid request.",
                ModbusErrorKind.InvalidRequest);

        /// <summary>
        /// 失败结果（非 Modbus 异常响应）是否必须重建连接才能重试。
        /// TCP：ErrorKind 为 Timeout 或 ConnectionClosed 时返回 true；RTU 保持现状：从不（基类默认 false）。
        /// </summary>
        protected virtual bool RequiresNewConnection(ModbusResult<byte[]> result) => false;

        // ————————————————— IModbus 公开 API（基类实现） —————————————————

        public bool Connect()
        {
            ThrowIfDisposed();

            requestLock.Wait();
            try
            {
                return OpenConnection();
            }
            finally
            {
                requestLock.Release();
            }
        }

        public async Task<bool> ConnectAsync()
        {
            ThrowIfDisposed();

            await requestLock.WaitAsync();
            try
            {
                return await OpenConnectionAsync(CancellationToken.None);
            }
            finally
            {
                requestLock.Release();
            }
        }

        public void Disconnect()
        {
            requestLock.Wait();
            try
            {
                CloseConnection();
            }
            finally
            {
                requestLock.Release();
            }
        }

        public ModbusResult<byte[]> Request(ModbusRequest request)
        {
            LogRequestStarted(request, isAsync: false);

            if (!ModbusHelper.CheckRequest(request))
                return CreateInvalidRequestResult(request, isAsync: false);

            requestLock.Wait();
            try
            {
                if (AssignsTransactionId)
                    request.TransactionId = transactionId++;
                return ExecuteRequestWithRetry(request);
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                Logger.LogError(ex, GetRequestFailedLogText(isAsync: false));
                InvalidateConnection();
                return ModbusResult<byte[]>.Fail($" [Request] Request failed: {ex.Message}", ClassifyCommunicationException(ex));
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
            LogRequestStarted(request, isAsync: true);

            if (!ModbusHelper.CheckRequest(request))
                return CreateInvalidRequestResult(request, isAsync: true);

            var lockTaken = false;
            try
            {
                await requestLock.WaitAsync(cancellationToken);
                lockTaken = true;

                if (AssignsTransactionId)
                    request.TransactionId = transactionId++;
                return await ExecuteRequestWithRetryAsync(request, cancellationToken);
            }
            catch (OperationCanceledException ex)
            {
                Logger.LogWarning(ex, " [RequestAsync] Request cancelled.");
                return ModbusResult<byte[]>.Fail(" [RequestAsync] Request cancelled.", ModbusErrorKind.Cancelled);
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                Logger.LogError(ex, GetRequestFailedLogText(isAsync: true));
                InvalidateConnection();
                return ModbusResult<byte[]>.Fail($" [RequestAsync] Request failed: {ex.Message}", ClassifyCommunicationException(ex));
            }
            finally
            {
                if (lockTaken)
                    requestLock.Release();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            DisposeConnection();
            requestLock.Dispose();
            Logger.LogDebug(" [Dispose] {Transport} disposed.", GetType().Name);
        }

        // ————————————————— 重试骨架（同步 + 异步，唯一两份） —————————————————

        private ModbusResult<byte[]> ExecuteRequestWithRetry(ModbusRequest request)
        {
            ModbusResult<byte[]> lastResult = ModbusResult<byte[]>.Fail("Request was not executed.");
            int attempts = GetAttemptCount();

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                // 规则 A：未连接且不允许自动重连 → 立即返回，不等待、不再尝试。
                if (!IsConnected && !ReconnectEnabled)
                    return ModbusResult<byte[]>.Fail(GetNotConnectedMessage(isAsync: false), ModbusErrorKind.ConnectionClosed);

                if (!EnsureConnected())
                {
                    lastResult = ModbusResult<byte[]>.Fail(GetNotConnectedMessage(isAsync: false), ModbusErrorKind.ConnectionClosed);
                    if (attempt < attempts)
                    {
                        WaitBeforeRetry();
                        continue;
                    }

                    return lastResult;
                }

                Logger.LogDebug(" [Request] Attempt {Attempt}/{Attempts}: {@Request}.", attempt, attempts, request);

                try
                {
                    if (!SendFrame(request))
                    {
                        // 规则 B：发送失败销毁连接；Reconnect=false 时立即返回，不再尝试。
                        lastResult = ModbusResult<byte[]>.Fail(GetSendFailedMessage(isAsync: false), ModbusErrorKind.ConnectionClosed);
                        InvalidateConnection();
                        if (!ReconnectEnabled)
                            return lastResult;
                    }
                    else
                    {
                        lastResult = ReceiveFrame(request);
                        if (lastResult.IsSuccess)
                            return lastResult;  // 规则 F：成功

                        // 规则 E：Modbus exception responses are terminal answers — return immediately, no resend.
                        if (lastResult.ErrorKind == ModbusErrorKind.ModbusException)
                            return lastResult;

                        Logger.LogWarning(" [Request] Attempt {Attempt}/{Attempts} failed: {Error}.", attempt, attempts, lastResult.ErrorMessage);

                        // 规则 C：需要重建连接的失败（超时、连接关闭）→ 销毁连接；Reconnect=false 时立即返回真实 ErrorKind。
                        if (RequiresNewConnection(lastResult))
                        {
                            InvalidateConnection();
                            if (!ReconnectEnabled)
                                return lastResult;
                        }
                        // 规则 D：无需换连接的失败 → 同一连接上等待后重试，次数受 RetryCount 限制。
                    }
                }
                catch (Exception ex) when (IsCommunicationException(ex))
                {
                    Logger.LogWarning(ex, " [Request] Attempt {Attempt}/{Attempts} failed.", attempt, attempts);
                    lastResult = ModbusResult<byte[]>.Fail($" [Request] {ex.Message}", ClassifyCommunicationException(ex));
                    // 规则 B：通信异常已破坏连接；Reconnect=false 时立即返回，不再尝试。
                    InvalidateConnection();
                    if (!ReconnectEnabled)
                        return lastResult;
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

                // 规则 A：未连接且不允许自动重连 → 立即返回，不等待、不再尝试。
                if (!IsConnected && !ReconnectEnabled)
                    return ModbusResult<byte[]>.Fail(GetNotConnectedMessage(isAsync: true), ModbusErrorKind.ConnectionClosed);

                if (!await EnsureConnectedAsync(cancellationToken))
                {
                    lastResult = ModbusResult<byte[]>.Fail(GetNotConnectedMessage(isAsync: true), ModbusErrorKind.ConnectionClosed);
                    if (attempt < attempts)
                    {
                        await WaitBeforeRetryAsync(cancellationToken);
                        continue;
                    }

                    return lastResult;
                }

                Logger.LogDebug(" [RequestAsync] Attempt {Attempt}/{Attempts}: {@Request}.", attempt, attempts, request);

                try
                {
                    if (!await SendFrameAsync(request, cancellationToken))
                    {
                        // 规则 B：发送失败销毁连接；Reconnect=false 时立即返回，不再尝试。
                        lastResult = ModbusResult<byte[]>.Fail(GetSendFailedMessage(isAsync: true), ModbusErrorKind.ConnectionClosed);
                        InvalidateConnection();
                        if (!ReconnectEnabled)
                            return lastResult;
                    }
                    else
                    {
                        lastResult = await ReceiveFrameAsync(request, cancellationToken);
                        if (lastResult.IsSuccess)
                            return lastResult;  // 规则 F：成功

                        // 规则 E：Modbus exception responses are terminal answers — return immediately, no resend.
                        if (lastResult.ErrorKind == ModbusErrorKind.ModbusException)
                            return lastResult;

                        Logger.LogWarning(" [RequestAsync] Attempt {Attempt}/{Attempts} failed: {Error}.", attempt, attempts, lastResult.ErrorMessage);

                        // 规则 C：需要重建连接的失败（超时、连接关闭）→ 销毁连接；Reconnect=false 时立即返回真实 ErrorKind。
                        if (RequiresNewConnection(lastResult))
                        {
                            InvalidateConnection();
                            if (!ReconnectEnabled)
                                return lastResult;
                        }
                        // 规则 D：无需换连接的失败 → 同一连接上等待后重试，次数受 RetryCount 限制。
                    }
                }
                catch (Exception ex) when (IsCommunicationException(ex))
                {
                    Logger.LogWarning(ex, " [RequestAsync] Attempt {Attempt}/{Attempts} failed.", attempt, attempts);
                    lastResult = ModbusResult<byte[]>.Fail($" [RequestAsync] {ex.Message}", ClassifyCommunicationException(ex));
                    // 规则 B：通信异常已破坏连接；Reconnect=false 时立即返回，不再尝试。
                    InvalidateConnection();
                    if (!ReconnectEnabled)
                        return lastResult;
                }

                if (attempt < attempts)
                    await WaitBeforeRetryAsync(cancellationToken);
            }

            return lastResult;
        }

        // ————————————————— 重连/重试辅助（上移） —————————————————

        private bool EnsureConnected()
        {
            if (IsConnected)
                return true;

            if (!ReconnectEnabled)
                return false;

            LogReconnectAttempt(isAsync: false);
            try
            {
                return OpenConnection();               // 已在请求锁内，走无锁核心，避免重入死锁
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                LogReconnectFailed(ex, isAsync: false);
                return false;
            }
        }

        private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (IsConnected)
                return true;

            if (!ReconnectEnabled)
                return false;

            LogReconnectAttempt(isAsync: true);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 调用方已持有 requestLock；OpenConnectionAsync 是无锁核心，不会重入。
                return await OpenConnectionAsync(cancellationToken);
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                LogReconnectFailed(ex, isAsync: true);
                return false;
            }
        }

        private int GetAttemptCount()
            => Math.Max(1, RetryCount + 1);

        private void WaitBeforeRetry()
        {
            if (RetryInterval > 0)
                Thread.Sleep(RetryInterval);
        }

        private Task WaitBeforeRetryAsync(CancellationToken cancellationToken)
        {
            return RetryInterval > 0
                ? Task.Delay(RetryInterval, cancellationToken)
                : Task.CompletedTask;
        }

        // ————————————————— 公共辅助 —————————————————

        /// <summary>封装 stopwatch/lastTimestamp 状态的 TX 日志。</summary>
        protected void LogTx(string name, byte[] data)
            => Logger.Tx(name, data, stopwatch, ref lastTimestamp);

        /// <summary>封装 stopwatch/lastTimestamp 状态的 RX 日志。</summary>
        protected void LogRx(string name, ReadOnlySpan<byte> data)
            => Logger.Rx(name, data, stopwatch, ref lastTimestamp);

        protected void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(GetType().Name);
        }

        /// <summary>
        /// 通信异常按类型归类：TimeoutException → Timeout，其余 → ConnectionClosed。
        /// </summary>
        private static ModbusErrorKind ClassifyCommunicationException(Exception ex)
            => ex is TimeoutException ? ModbusErrorKind.Timeout : ModbusErrorKind.ConnectionClosed;

        /// <summary>
        /// 通信异常判定（原 TCP/RTU 两表合并为并集）：
        /// TCP 原表 —— SocketException || IOException || ObjectDisposedException || InvalidOperationException || EndOfStreamException；
        /// RTU 原表 —— TimeoutException || IOException || InvalidOperationException || UnauthorizedAccessException || ObjectDisposedException。
        /// 合并取舍：<see cref="InvalidOperationException"/> 保留在表内（串口未打开场景）；
        /// 并集使 TCP 路径额外捕获 TimeoutException、RTU 路径额外捕获 SocketException/EndOfStreamException
        /// （RTU 不使用 Socket，实际无影响）。注意 EndOfStreamException 是 IOException 的子类，显式列出仅为对齐原表。
        /// </summary>
        protected static bool IsCommunicationException(Exception ex)
        {
            return ex is SocketException
                || ex is TimeoutException
                || ex is IOException
                || ex is ObjectDisposedException
                || ex is UnauthorizedAccessException
                || ex is InvalidOperationException
                || ex is EndOfStreamException;
        }
    }
}
