using System.Security.Cryptography.X509Certificates;
using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Hosting;
using Junevy.Communication.WebApi.Server.Pipeline;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Lifecycle;

/// <summary>
/// 服务器的生命周期（内部，P32-D4、D12、D13）：状态机、启动、停止与释放。
/// 启动、停止与释放都在同一把异步锁之下串行执行，因此状态变化与事件的顺序与实际变化一致。
/// 锁本身不释放：在途的操作可能仍需要它（P6.1 R3、P6.2 R10）。
/// </summary>
internal sealed class ServerLifecycle
{
    private readonly string name;
    private readonly WebApiServerSettings settings;
    private readonly ServerEvents events;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
    private readonly object stateLock = new object();
    private WebApiServerState state = WebApiServerState.Stopped;
    private KestrelHost? host;
    private X509Certificate2? ownedCertificate;
    private bool startRequested;
    private bool disposed;

    /// <summary>创建生命周期。</summary>
    /// <param name="name">服务器名称。</param>
    /// <param name="settings">已校验的配置。</param>
    /// <param name="events">请求事件。</param>
    /// <param name="loggerFactory">调用方的日志工厂。</param>
    /// <param name="logger">服务器的日志记录器。</param>
    public ServerLifecycle(string name, WebApiServerSettings settings, ServerEvents events, ILoggerFactory loggerFactory, ILogger logger)
    {
        this.name = name;
        this.settings = settings;
        this.events = events;
        this.loggerFactory = loggerFactory;
        this.logger = logger;
    }

    /// <summary>状态变化事件。</summary>
    public event EventHandler<WebApiServerStateChangedEventArgs>? StateChanged;

    /// <summary>当前状态。</summary>
    public WebApiServerState State
    {
        get
        {
            lock (stateLock)
                return state;
        }
    }

    /// <summary>是否已经请求过启动（之后不再接受路由注册）。</summary>
    public bool StartRequested
    {
        get
        {
            lock (stateLock)
                return startRequested;
        }
    }

    /// <summary>是否已经释放。</summary>
    public bool IsDisposed
    {
        get
        {
            lock (stateLock)
                return disposed;
        }
    }

    /// <summary>运行中的应用宿主（用于测试断言在途数）；未运行时为 null。</summary>
    internal KestrelHost? Host
    {
        get
        {
            lock (stateLock)
                return host;
        }
    }

    /// <summary>监听地址：运行中为实际地址，否则为配置值。</summary>
    public IReadOnlyList<string> Urls
    {
        get
        {
            lock (stateLock)
                return host?.Addresses ?? settings.Urls;
        }
    }

    /// <summary>启动服务器。</summary>
    /// <param name="routes">路由快照。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启动结果。</returns>
    public async Task<CommResult> StartAsync(IReadOnlyList<RouteDefinition> routes, CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return CommResult.Fail("The server start was cancelled.", CommErrorKind.Cancelled, exception: ex);
        }

        try
        {
            return await StartCoreAsync(routes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>停止服务器（可重复调用；已释放后无操作）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>停止完成的任务。</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed)
            return;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>停止并释放。可重复调用。</summary>
    /// <returns>释放完成的任务。</returns>
    public async ValueTask DisposeAsync()
    {
        if (IsDisposed)
            return;

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lock (stateLock)
            disposed = true;
    }

    private async Task<CommResult> StartCoreAsync(IReadOnlyList<RouteDefinition> routes, CancellationToken cancellationToken)
    {
        if (State != WebApiServerState.Stopped)
            return CommResult.Fail("The server is already started.", CommErrorKind.InvalidRequest);
        if (routes.Count == 0)
            return CommResult.Fail("No route is registered; map at least one route before starting the server.", CommErrorKind.InvalidRequest);

        lock (stateLock)
            startRequested = true;
        ChangeState(WebApiServerState.Starting, "start requested", null);

        CommResult? certificateFailure = ResolveCertificate(out X509Certificate2? certificate);
        if (certificateFailure != null)
            return Abort(certificateFailure);

        KestrelHost candidate;
        try
        {
            candidate = KestrelHost.Create(settings, name, routes, events, loggerFactory, certificate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Abort(CommResult.Fail("The routes could not be mapped: " + ex.Message, CommErrorKind.InvalidRequest, exception: ex));
        }

        try
        {
            await candidate.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await candidate.DisposeAsync().ConfigureAwait(false);
            return Abort(StartFailure.Classify(ex));
        }

        lock (stateLock)
            host = candidate;
        ChangeState(WebApiServerState.Running, "listening on " + string.Join(", ", candidate.Addresses), null);
        WarnIfUnprotected();
        return CommResult.Success();
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        KestrelHost? running;
        lock (stateLock)
            running = state == WebApiServerState.Running ? host : null;
        if (running == null)
            return;

        ChangeState(WebApiServerState.Stopping, "stop requested", null);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(settings.StopTimeout);
            try
            {
                await running.StopAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The server {Name} did not stop cleanly.", name);
            }
        }

        await running.DisposeAsync().ConfigureAwait(false);
        lock (stateLock)
            host = null;
        ReleaseOwnedCertificate();
        ChangeState(WebApiServerState.Stopped, "stopped", null);
    }

    // 启动失败：回到 Stopped，释放自己加载的证书，不留下任何应用。
    private CommResult Abort(CommResult failure)
    {
        ReleaseOwnedCertificate();
        ChangeState(WebApiServerState.Stopped, failure.ErrorMessage, failure.Exception);
        return failure;
    }

    private CommResult? ResolveCertificate(out X509Certificate2? certificate)
    {
        certificate = settings.Https.ProvidedCertificate;
        if (certificate != null || settings.Https.CertificatePath == null)
            return null;

        string path = settings.Https.CertificatePath;
        try
        {
            certificate = CertificateLoader.Load(path, settings.Https.CertificatePassword);
            ownedCertificate = certificate;
            return null;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            return CommResult.Fail($"The HTTPS certificate could not be loaded from '{path}'.", CommErrorKind.InvalidRequest, exception: ex);
        }
    }

    private void ReleaseOwnedCertificate()
    {
        ownedCertificate?.Dispose();
        ownedCertificate = null;
    }

    private void WarnIfUnprotected()
    {
        bool authenticated = settings.CustomAuthenticator != null || settings.Credentials.Scheme != Junevy.Communication.WebApi.WebApiAuthScheme.None;
        if (ServerExposureRules.IsUnprotectedWildcard(settings.Urls, authenticated))
            logger.LogWarning("The server {Name} listens on all interfaces without authentication; configure Auth or a custom authenticator, or bind a specific address.", name);
    }

    private void ChangeState(WebApiServerState next, string? reason, Exception? exception)
    {
        WebApiServerState previous;
        lock (stateLock)
        {
            previous = state;
            state = next;
        }

        logger.LogInformation("The server {Name} changed state from {Previous} to {Current}.", name, previous, next);
        EventSubscribers.InvokeEach(StateChanged, this, new WebApiServerStateChangedEventArgs(name, previous, next, reason, exception), logger, nameof(StateChanged));
    }
}
