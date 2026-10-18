using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Junevy.Communication.WebApi.Server.Auth;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Dedup;
using Junevy.Communication.WebApi.Server.Logging;
using Junevy.Communication.WebApi.Server.Pipeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Hosting;

/// <summary>
/// 嵌入式 Kestrel 应用（内部，P32-D3）：一个实例对应一次启动。应用只含 Kestrel、路由与本库的中间件，
/// 不读取宿主的 <c>appsettings.json</c>、环境变量与命令行，不添加默认的日志提供程序。
/// 中间件顺序：入口与出口 → 并发限流 → 路由 → 端点（管线）。
/// </summary>
internal sealed class KestrelHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly InFlightCounter inFlight;
    private readonly AbandonedWork abandoned;
    private readonly DeduplicationCache? dedup;

    private KestrelHost(WebApplication app, InFlightCounter inFlight, AbandonedWork abandoned, DeduplicationCache? dedup)
    {
        this.app = app;
        this.inFlight = inFlight;
        this.abandoned = abandoned;
        this.dedup = dedup;
    }

    /// <summary>当前在途的请求数（已经发出响应、被摘出去的处理器不计入）。</summary>
    public int InFlightCount => inFlight.Count;

    /// <summary>被摘出去（超时或对端断开之后）仍在运行的处理器数。</summary>
    public int AbandonedCount => abandoned.Count;

    /// <summary>去重缓存的条目数；去重关闭时为 0。</summary>
    public int DeduplicationEntryCount => dedup?.EntryCount ?? 0;

    /// <summary>去重缓存中处理中的条目数；去重关闭时为 0。</summary>
    public int DeduplicationInFlightCount => dedup?.InFlightCount ?? 0;

    /// <summary>启动后实际绑定的地址（端口 0 已解析），在启动成功时拍下快照；未启动时为空。</summary>
    public IReadOnlyList<string> Addresses { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// 建立应用（尚未启动）。路由必须在此之前全部登记。路由映射失败时释放已建立的应用再抛出。
    /// </summary>
    /// <param name="services">服务器共用的对象。</param>
    /// <param name="routes">路由定义。</param>
    /// <param name="certificate">HTTPS 证书；没有 https 地址时为 null。</param>
    /// <returns>应用宿主。</returns>
    public static KestrelHost Create(HostServices services, IEnumerable<RouteDefinition> routes, X509Certificate2? certificate)
    {
        WebApiServerSettings settings = services.Settings;
        WebApplication app = CreateBuilder(settings, services.LoggerFactory, certificate).Build();
        try
        {
            DeduplicationCache? dedup = CreateDeduplication(settings);
            var abandoned = new AbandonedWork(services.Logger);
            var log = new RequestLog(settings.Logging, services.Logger);
            var results = new ServerResults(services.Formatter, services.Logger);
            var pipeline = new RequestPipeline(
                settings,
                results,
                services.Logger,
                ServerAuthenticator.Create(settings, services.ServerName),
                dedup,
                abandoned,
                log);
            var inFlight = new InFlightCounter();

            app.Use(new ServerEntryMiddleware(services.ServerName, services.Events, pipeline, results, inFlight, log, services.Logger).InvokeAsync);
            app.UseRateLimiter();
            app.UseRouting();
            foreach (RouteDefinition route in routes)
                RouteEndpointBinder.Map(app, route, pipeline, services.ServerName, settings.MaxRequestBodySize);

            return new KestrelHost(app, inFlight, abandoned, dedup);
        }
        catch
        {
            ((IDisposable)app).Dispose();
            throw;
        }
    }

    /// <summary>启动应用并绑定地址，成功后拍下实际地址。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启动完成的任务；绑定失败时抛出异常。</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        IServerAddressesFeature? feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        Addresses = feature == null ? Array.Empty<string>() : feature.Addresses.ToArray();
    }

    /// <summary>停止应用：停止接收，等待在途请求，超过令牌的期限后中止剩余连接。</summary>
    /// <param name="cancellationToken">停止的期限。</param>
    /// <returns>停止完成的任务。</returns>
    public Task StopAsync(CancellationToken cancellationToken) => app.StopAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private static DeduplicationCache? CreateDeduplication(WebApiServerSettings settings)
        => settings.Deduplication.Enabled
            ? new DeduplicationCache(settings.Deduplication.Window, settings.Deduplication.MaxEntries, settings.TimeProvider)
            : null;

    private static WebApplicationBuilder CreateBuilder(WebApiServerSettings settings, ILoggerFactory loggerFactory, X509Certificate2? certificate)
    {
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.UseUrls(settings.Urls);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = settings.MaxRequestBodySize;
            if (certificate != null)
                options.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate);
        });
        if (certificate != null)
            builder.WebHost.UseKestrelHttpsConfiguration();
        builder.Services.AddRoutingCore();
        builder.Services.AddRateLimiter(options => ConfigureConcurrencyLimit(options, settings.MaxConcurrentRequests));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddProvider(new ForwardingLoggerProvider(loggerFactory));
        return builder;
    }

    // 并发限流：同时处理的请求超过上限时立即拒绝，不排队（队列长度 0）；拒绝时只打标记，由出口写出本库格式的 503。
    private static void ConfigureConcurrencyLimit(RateLimiterOptions options, int permitLimit)
    {
        options.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
            RateLimitPartition.GetConcurrencyLimiter("all", _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = permitLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));
        options.OnRejected = (context, _) =>
        {
            context.HttpContext.Items[ServerKeys.LimiterRejected] = true;
            return ValueTask.CompletedTask;
        };
    }
}
