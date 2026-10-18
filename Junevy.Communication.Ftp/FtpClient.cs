using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Ftp;

/// <summary>
/// FTP / FTPS 客户端（只做客户端，只用被动模式与二进制传输；P63-D2）。
/// 控制连接按需建立：每个操作开始时未连接就先连接，因此可以不调用 <see cref="ConnectAsync"/>（P63-D3）。
/// </summary>
/// <remarks>
/// <para>错误一律走结果（<see cref="CommResult"/>），SDK 抛出的异常不会逃出公开方法。参数为 null 抛
/// <see cref="ArgumentNullException"/>。释放之后 <see cref="ConnectAsync"/> 与 <see cref="DisconnectAsync"/> 抛
/// <see cref="ObjectDisposedException"/>，其余操作返回 <see cref="CommErrorKind.ConnectionClosed"/>。
/// <see cref="Dispose"/> 与 <see cref="DisposeAsync"/> 可以重复调用。</para>
/// <para>同一个客户端上的操作一次只执行一个（FTP 的控制连接一次只能执行一条命令，P63-D4）：并发调用会排队，不会失败。
/// 需要并行传输时，宿主创建多个客户端。</para>
/// <para>本类是门面：生命周期（连接、断开、释放、串行门）在 <c>FtpLifecycle</c>，SDK 的交互在 <c>Sdk/</c>。</para>
/// </remarks>
public sealed partial class FtpClient : IDisposable, IAsyncDisposable
{
    private readonly FtpLifecycle lifecycle;

    private FtpClient(FtpConnectionSettings settings, ILoggerFactory loggerFactory)
    {
        if (settings.AllowUntrustedCertificates)
        {
            loggerFactory.CreateLogger<FtpClient>().LogWarning(
                "FtpClientOptions.AllowUntrustedCertificates is enabled: any server certificate will be accepted.");
        }

        lifecycle = new FtpLifecycle(settings, loggerFactory);
    }

    /// <summary>按选项创建客户端，并在此时校验选项、取快照。</summary>
    /// <param name="options">连接选项；不能为 null。之后修改这个对象不影响已创建的客户端。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <returns>未连接的客户端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException">某个选项取值非法，消息写明属性名与允许范围。</exception>
    public static FtpClient Create(FtpClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        FtpClientOptionsValidator.Validate(options);

        return new FtpClient(FtpConnectionSettings.From(options), loggerFactory ?? NullLoggerFactory.Instance);
    }

    /// <summary>控制连接当前是否已建立。释放之后为 false。</summary>
    public bool IsConnected => lifecycle.IsConnected;

    /// <summary>建立控制连接并登录。已连接时直接返回成功，不重新连接。</summary>
    /// <param name="cancellationToken">取消令牌；被触发时返回 <see cref="CommErrorKind.Cancelled"/>。</param>
    /// <returns>已连接时成功；登录失败为 <see cref="CommErrorKind.AuthenticationFailed"/>（协议码 530）。</returns>
    /// <exception cref="ObjectDisposedException">客户端已释放。</exception>
    public async Task<CommResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await lifecycle.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>断开控制连接（发送 QUIT）。未连接时什么也不做，可以重复调用。</summary>
    /// <param name="cancellationToken">取消令牌；被触发时抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>断开完成。</returns>
    /// <exception cref="ObjectDisposedException">客户端已释放。</exception>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await lifecycle.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>释放客户端：取消正在进行的操作，发送 QUIT（尽力而为）并关闭控制连接。可以重复调用。</summary>
    /// <remarks>不要在进度回调里调用同步的 <see cref="Dispose"/>：它会等待正在进行的传输结束。</remarks>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>异步释放客户端。可以重复调用，每次都等待同一次释放完成。</summary>
    /// <returns>释放完成。</returns>
    public ValueTask DisposeAsync() => lifecycle.DisposeAsync();

    // 路径操作的公共流程：先检查参数（null 抛异常；空路径返回 InvalidRequest 且不连接），再在串行门内执行。
    private async Task<CommResult<T>> PathOperationAsync<T>(
        string path,
        string parameter,
        Func<FtpSdkSession, string, CancellationToken, Task<CommResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        if (FtpRequests.CheckPath(path, parameter) is { } invalid)
        {
            return invalid.As<T>();
        }

        return await lifecycle.RunAsync<T>((session, token) => operation(session, path, token), cancellationToken)
            .ConfigureAwait(false);
    }

    // 不返回值的路径操作：SDK 失败时抛出的异常由生命周期翻译为结果。
    private async Task<CommResult> PathActionAsync(
        string path,
        string parameter,
        Func<FtpSdkSession, string, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        CommResult<bool> result = await PathOperationAsync<bool>(
            path,
            parameter,
            async (session, target, token) =>
            {
                await operation(session, target, token).ConfigureAwait(false);
                return CommResult<bool>.Success(true);
            },
            cancellationToken).ConfigureAwait(false);
        return result.ToResult();
    }

    private void ThrowIfDisposed()
    {
        if (lifecycle.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(FtpClient));
        }
    }
}
