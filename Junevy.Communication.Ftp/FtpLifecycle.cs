using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Sdk;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Ftp;

/// <summary>
/// 客户端的生命周期：控制连接的按需建立与断开、每个操作共用的串行门、释放（P63-D3、D4，P6-C8）。
/// 公开类只做参数检查与转发；SDK 的异常在这里翻译成结果，分类本身只在 <see cref="FtpErrorMap"/> 中决定。
/// </summary>
internal sealed class FtpLifecycle
{
    private readonly FtpSdkSession session;

    // 串行门：一次只允许一个操作使用控制连接（P63-D4）。信号量从不释放：释放客户端时仍可能有操作在等待或持有它，
    // 而没有访问过 AvailableWaitHandle 的 SemaphoreSlim 不持有非托管资源。
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource disposing = new();
    private readonly Lazy<Task> disposal;
    private readonly ILogger logger;
    private readonly int replyTimeout;
    private int disposed;

    public FtpLifecycle(FtpConnectionSettings settings, ILoggerFactory loggerFactory)
    {
        session = new FtpSdkSession(settings, loggerFactory);
        logger = loggerFactory.CreateLogger<FtpLifecycle>();
        replyTimeout = settings.ReadTimeout;
        disposal = new Lazy<Task>(DisposeCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>控制连接是否已建立。</summary>
    public bool IsConnected => !IsDisposed && session.IsConnected;

    /// <summary>建立控制连接并登录（已连接时直接成功）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接结果。</returns>
    public async Task<CommResult> ConnectAsync(CancellationToken cancellationToken)
    {
        CommResult<bool> result = await RunAsync<bool>(
            new FtpOperation("Connect", "control connection"),
            static (_, _) => Task.FromResult(CommResult<bool>.Success(true)),
            cancellationToken).ConfigureAwait(false);
        return result.ToResult();
    }

    /// <summary>断开控制连接（QUIT 后关闭）。未连接时什么也不做。</summary>
    /// <param name="cancellationToken">取消令牌；等待串行门或断开时被触发会抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>断开完成。</returns>
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsDisposed || !session.IsConnected)
            {
                return;
            }

            try
            {
                await session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                logger.LogDebug("The FTP control connection was closed.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // QUIT 失败不影响断开：SDK 已经关闭连接，这里只记录。
                logger.LogWarning(ex, "The FTP QUIT command failed; the control connection was closed locally.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 在串行门内执行一个操作：未连接时先按需连接（P63-D3），再执行，任何失败都翻译成结果。
    /// 释放之后直接返回 <see cref="CommErrorKind.ConnectionClosed"/>。每个操作结束时记录一条日志（P63-D15）。
    /// </summary>
    /// <typeparam name="T">操作的返回值类型（非空引用或值类型）。</typeparam>
    /// <param name="operation">操作的日志信息（操作名、目标、字节数与耗时）。</param>
    /// <param name="run">要执行的 SDK 操作；收到的令牌在调用方取消、客户端释放或超时时触发。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    /// <returns>操作的结果。</returns>
    public async Task<CommResult<T>> RunAsync<T>(
        FtpOperation operation,
        Func<FtpSdkSession, CancellationToken, Task<CommResult<T>>> run,
        CancellationToken cancellationToken)
    {
        CommResult<T> result = await RunGatedAsync(operation, run, cancellationToken).ConfigureAwait(false);
        operation.LogCompletion(logger, result.ToResult());
        return result;
    }

    /// <summary>释放客户端。可以重复调用，每次都等待同一次释放完成。</summary>
    /// <returns>释放完成。</returns>
    public ValueTask DisposeAsync() => new(disposal.Value);

    private async Task<CommResult<T>> RunGatedAsync<T>(
        FtpOperation operation,
        Func<FtpSdkSession, CancellationToken, Task<CommResult<T>>> run,
        CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            return FtpErrorMap.Disposed().As<T>();
        }

        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FtpErrorMap.Cancelled().As<T>();
        }

        try
        {
            operation.Start();
            return await ExecuteAsync(run, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    // 连接级失败（断线或超时）之后不在这里关闭连接：超时后 SDK 已经把自己置为未连接（第 18.2 节 F31），
    // 下一次操作按需连接；服务器关闭的连接由下一次操作前的存活检查发现（F28）。
    private async Task<CommResult<T>> ExecuteAsync<T>(
        Func<FtpSdkSession, CancellationToken, Task<CommResult<T>>> run,
        CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            return FtpErrorMap.Disposed().As<T>();
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposing.Token);
        try
        {
            await EnsureConnectedAsync(linked.Token).ConfigureAwait(false);
            return await run(session, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure<T>(ex, cancellationToken);
        }
    }

    // SDK 的 IsConnected 不会察觉服务器已经关闭控制连接（第 18.2 节 F28）：已连接时先确认连接还活着；
    // 已经断开就关闭 SDK 的状态，再重新连接（P63-D3）。
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (session.IsConnected)
        {
            if (await session.IsAliveAsync(replyTimeout, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (session.IsConnected)
            {
                await QuitBestEffortAsync().ConfigureAwait(false);
            }
        }

        await ConnectSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    // 调用方取消优先于释放：两者同时发生时以调用方的取消为准。
    private CommResult<T> Failure<T>(Exception exception, CancellationToken cancellationToken)
    {
        CommResult result = cancellationToken.IsCancellationRequested
            ? FtpErrorMap.Cancelled()
            : IsDisposed
                ? FtpErrorMap.Disposed()
                : FtpErrorMap.FromException(exception);
        return result.As<T>();
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        disposing.Cancel();

        // 等待正在进行的操作因取消而结束，然后独占串行门做最后的 QUIT。
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (session.IsConnected)
            {
                await QuitBestEffortAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    // 登录失败后 SDK 仍保持控制连接：失败时关闭它，使 IsConnected 与套接字状态一致。
    private async Task ConnectSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await QuitBestEffortAsync().ConfigureAwait(false);
            throw;
        }

        logger.LogDebug("The FTP control connection was established.");
    }

    private async Task QuitBestEffortAsync()
    {
        using var timeout = new CancellationTokenSource(replyTimeout);
        try
        {
            await session.DisconnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("The best-effort FTP QUIT timed out; the control connection was closed locally.");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The best-effort FTP QUIT failed; the control connection was closed locally.");
        }
    }
}
