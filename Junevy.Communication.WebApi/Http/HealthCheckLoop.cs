using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 周期性健康检查（内部，计划 Task 7 第 2 条）：构造后立即开始，每个间隔对健康检查地址发一次 GET。
/// 探测复用单次尝试执行器，因此走拦截器链与超时；但它不重试、使用一个没有订阅者的事件集合（不触发请求事件），
/// 结果只报告给可用性跟踪。任何响应都说明对端可达，包括 4xx / 5xx（502 / 503 / 504 由跟踪器判为失败）。
/// 探测的调用序号固定为 0，不占用用户调用的序号。
/// </summary>
internal sealed class HealthCheckLoop
{
    private const long ProbeCallId = 0;

    private readonly AttemptRunner runner;
    private readonly HttpRequestSnapshot snapshot;
    private readonly Uri healthUri;
    private readonly EndpointSettings settings;
    private readonly IAvailabilityReporter availability;
    private readonly ILogger logger;
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly Task loop;
    private int stopped;

    private HealthCheckLoop(AttemptRunner runner, Uri healthUri, EndpointSettings settings, IAvailabilityReporter availability, ILogger logger)
    {
        this.runner = runner;
        this.healthUri = healthUri;
        this.settings = settings;
        this.availability = availability;
        this.logger = logger;
        snapshot = HttpRequestSnapshot.Create(settings, "GET", string.Empty, null, null, Array.Empty<byte>(), null);
        loop = Task.Run(() => RunAsync(stop.Token));
    }

    /// <summary>创建并立即开始探测。</summary>
    /// <param name="runner">单次尝试执行器（配套的事件集合没有订阅者）。</param>
    /// <param name="healthUri">健康检查的绝对地址。</param>
    /// <param name="settings">端点配置快照（间隔、超时与默认头）。</param>
    /// <param name="availability">接收探测结果的可用性跟踪。</param>
    /// <param name="logger">日志记录器。</param>
    /// <returns>正在运行的健康检查。</returns>
    public static HealthCheckLoop Start(AttemptRunner runner, Uri healthUri, EndpointSettings settings, IAvailabilityReporter availability, ILogger logger)
        => new HealthCheckLoop(runner, healthUri, settings, availability, logger);

    /// <summary>
    /// 停止探测：取消正在进行的探测，并等待循环结束。可重复调用。
    /// </summary>
    /// <returns>循环结束的任务。</returns>
    public async Task StopAsync()
    {
        bool first = Interlocked.Exchange(ref stopped, 1) == 0;
        if (first)
            stop.Cancel();

        await loop.ConfigureAwait(false);
        if (first)
            stop.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await ProbeAsync(token).ConfigureAwait(false);
            try
            {
                await Task.Delay(settings.HealthCheckInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ProbeAsync(CancellationToken token)
    {
        try
        {
            var call = new CallState(ProbeCallId, snapshot, healthUri, healthUri) { Attempts = 1 };
            AttemptOutcome<int> outcome = await runner.RunAsync(call, settings.Timeout, Interpret, token).ConfigureAwait(false);
            if (outcome.Exchange != null)
                availability.ReportAttempt(outcome.Exchange.FailureKind, outcome.Exchange.StatusCode, outcome.Exchange.Exception);
        }
        catch (Exception ex)
        {
            // 边界：后台循环。一次探测的意外异常不能让健康检查从此停止。
            logger.LogError(ex, "A health check probe failed unexpectedly; the next probe still runs.");
        }
    }

    private static WebApiResult<int> Interpret(HttpExchangeResponse response) => WebApiResult<int>.Success(response.StatusCode, response.StatusCode);
}
