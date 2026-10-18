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
    private readonly FtpOperationGate gate = new();
    private readonly CancellationTokenSource disposing = new();
    private readonly Lazy<Task> disposal;
    private readonly ILogger logger;
    private readonly int quitTimeout;
    private int disposed;

    public FtpLifecycle(FtpConnectionSettings settings, ILoggerFactory loggerFactory)
    {
        session = new FtpSdkSession(settings, loggerFactory);
        logger = loggerFactory.CreateLogger<FtpLifecycle>();
        quitTimeout = settings.ReadTimeout;
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
            static (_, _) => Task.FromResult(CommResult<bool>.Success(true)),
            cancellationToken).ConfigureAwait(false);
        return result.ToResult();
    }

    /// <summary>断开控制连接（QUIT 后关闭）。未连接时什么也不做。</summary>
    /// <param name="cancellationToken">取消令牌；等待串行门或断开时被触发会抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>断开完成。</returns>
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await using (await gate.EnterAsync(cancellationToken).ConfigureAwait(false))
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
    }

    /// <summary>
    /// 在串行门内执行一个操作：未连接时先按需连接（P63-D3），再执行，任何失败都翻译成结果。
    /// 释放之后直接返回 <see cref="CommErrorKind.ConnectionClosed"/>。
    /// </summary>
    /// <typeparam name="T">操作的返回值类型（非空引用或值类型）。</typeparam>
    /// <param name="operation">要执行的 SDK 操作；收到的令牌在调用方取消、客户端释放或超时时触发。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    /// <returns>操作的结果。</returns>
    public async Task<CommResult<T>> RunAsync<T>(
        Func<FtpSdkSession, CancellationToken, Task<CommResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            return FtpErrorMap.Disposed().As<T>();
        }

        IAsyncDisposable lease;
        try
        {
            lease = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FtpErrorMap.Cancelled().As<T>();
        }

        await using (lease)
        {
            return await ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>释放客户端。可以重复调用，每次都等待同一次释放完成。</summary>
    /// <returns>释放完成。</returns>
    public ValueTask DisposeAsync() => new(disposal.Value);

    private async Task<CommResult<T>> ExecuteAsync<T>(
        Func<FtpSdkSession, CancellationToken, Task<CommResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            return FtpErrorMap.Disposed().As<T>();
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposing.Token);
        try
        {
            if (!session.IsConnected)
            {
                await ConnectSessionAsync(linked.Token).ConfigureAwait(false);
            }

            return await operation(session, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure<T>(ex, cancellationToken);
        }
    }

    // 调用方取消优先于释放：两者同时发生时以调用方的取消为准。
    private CommResult<T> Failure<T>(Exception exception, CancellationToken cancellationToken)
    {
        CommResult result = cancellationToken.IsCancellationRequested
            ? FtpErrorMap.Cancelled()
            : IsDisposed
                ? FtpErrorMap.Disposed()
                : FtpErrorMap.FromException(exception, cancelledByCaller: false);
        return result.As<T>();
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        disposing.Cancel();

        // 等待正在进行的操作因取消而结束，然后独占串行门做最后的 QUIT。
        await using (await gate.EnterAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (session.IsConnected)
            {
                await QuitBestEffortAsync().ConfigureAwait(false);
            }
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
        using var timeout = new CancellationTokenSource(quitTimeout);
        try
        {
            await session.DisconnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("The FTP QUIT during disposal timed out.");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The FTP QUIT during disposal failed.");
        }
    }
}
