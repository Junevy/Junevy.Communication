using Junevy.Communication.WebApi.Server.Auth;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Dedup;
using Junevy.Communication.WebApi.Server.Formatting;
using Junevy.Communication.WebApi.Server.Pipeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading.RateLimiting;

namespace Junevy.Communication.WebApi.Server.Hosting;

/// <summary>
/// 嵌入式 Kestrel 应用（内部，P32-D3）：一个实例对应一次启动。应用只含 Kestrel、路由与本库的中间件，
/// 不读取宿主的 <c>appsettings.json</c>、环境变量与命令行，不添加默认的日志提供程序。
/// 中间件顺序：入口与出口 → 并发限流 → 路由 → 端点（管线）。
/// </summary>
internal sealed class KestrelHost : IAsyncDisposable
{
    private const string LogCategory = "Junevy.Communication.WebApi.Server";

    private readonly WebApplication app;
    private readonly InFlightCounter inFlight;
    private readonly DeduplicationCache? dedup;

    private KestrelHost(WebApplication app, InFlightCounter inFlight, DeduplicationCache? dedup)
    {
        this.app = app;
        this.inFlight = inFlight;
        this.dedup = dedup;
    }

    /// <summary>当前在途的请求数。</summary>
    public int InFlightCount => inFlight.Count;

    /// <summary>去重缓存的条目数；去重关闭时为 0。</summary>
    public int DeduplicationEntryCount => dedup?.EntryCount ?? 0;

    /// <summary>去重缓存中处理中的条目数；去重关闭时为 0。</summary>
    public int DeduplicationInFlightCount => dedup?.InFlightCount ?? 0;

    /// <summary>
    /// 建立应用（尚未启动）。路由必须在此之前全部登记。
    /// </summary>
    /// <param name="settings">已校验的配置。</param>
    /// <param name="serverName">服务器名称。</param>
    /// <param name="routes">路由定义。</param>
    /// <param name="events">事件派发器。</param>
    /// <param name="loggerFactory">调用方的日志工厂。</param>
    /// <returns>应用宿主。</returns>
    public static KestrelHost Create(WebApiServerSettings settings, string serverName, IEnumerable<RouteDefinition> routes, ServerEvents events, ILoggerFactory loggerFactory)
    {
        WebApplication app = CreateBuilder(settings, loggerFactory).Build();
        ILogger logger = loggerFactory.CreateLogger(LogCategory);
        DeduplicationCache? dedup = CreateDeduplication(settings);
        var pipeline = new RequestPipeline(
            settings,
            new ResponseFormatter(settings),
            logger,
            ServerAuthenticator.Create(settings, serverName),
            settings.Interceptors,
            dedup);
        var inFlight = new InFlightCounter();

        app.Use(new ServerEntryMiddleware(serverName, events, pipeline, inFlight, logger).InvokeAsync);
        app.UseRateLimiter();
        app.UseRouting();
        foreach (RouteDefinition route in routes)
            RouteEndpointBinder.Map(app, route, pipeline, serverName, settings.MaxRequestBodySize);

        return new KestrelHost(app, inFlight, dedup);
    }

    /// <summary>启动应用并绑定地址。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启动完成的任务；绑定失败时抛出异常。</returns>
    public Task StartAsync(CancellationToken cancellationToken) => app.StartAsync(cancellationToken);

    /// <summary>停止应用：停止接收，等待在途请求，超过令牌的期限后中止剩余连接。</summary>
    /// <param name="cancellationToken">停止的期限。</param>
    /// <returns>停止完成的任务。</returns>
    public Task StopAsync(CancellationToken cancellationToken) => app.StopAsync(cancellationToken);

    /// <summary>启动后实际绑定的地址（端口 0 已解析）；未启动时为空。</summary>
    public IReadOnlyList<string> Addresses
    {
        get
        {
            IServerAddressesFeature? feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            return feature == null ? Array.Empty<string>() : feature.Addresses.ToArray();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private static DeduplicationCache? CreateDeduplication(WebApiServerSettings settings)
        => settings.Deduplication.Enabled
            ? new DeduplicationCache(settings.Deduplication.Window, settings.Deduplication.MaxEntries, settings.TimeProvider)
            : null;

    private static WebApplicationBuilder CreateBuilder(WebApiServerSettings settings, ILoggerFactory loggerFactory)
    {
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.UseUrls(settings.Urls);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = settings.MaxRequestBodySize);
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
