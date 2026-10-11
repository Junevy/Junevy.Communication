using System.Net.Http;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 健康检查（计划 Task 7 第 2 条）：构造后立即开始，周期性发 GET，走拦截器链与超时，不重试、不触发请求事件，结果只喂给可用性跟踪，释放时停止。
/// 健康检查在构造时就开始探测，因此每个用例先用 <see cref="TaskCompletionSource{TResult}"/> 把第一次探测卡在"闸门"上，订阅事件之后再放行。
/// </summary>
public sealed class HealthCheckTests
{
    private const string HealthUrl = "http://mes.local/api/health";
    private const int LongInterval = 600000;
    private const int Repeats = 50;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Probe_TurnsUnknownIntoOnline()
    {
        var gate = NewGate();
        await using var rig = new CoreRig(c => c.HealthCheckInterval = LongInterval, HealthUrl, handler => GatedResponse(handler, gate, 200));
        Assert.Equal(WebApiAvailability.Unknown, rig.Core.Availability);

        gate.SetResult(true);
        await rig.WaitForChangeAsync();

        Assert.Equal(WebApiAvailability.Online, rig.Core.Availability);
        WebApiAvailabilityChangedEventArgs change = Assert.Single(rig.Changes);
        Assert.Equal(WebApiAvailability.Unknown, change.PreviousAvailability);
        Assert.Equal(WebApiAvailability.Online, change.CurrentAvailability);
        StubRequest probe = rig.Handler.Requests.First();
        Assert.Equal("GET", probe.Method);
        Assert.Equal(HealthUrl, probe.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Probe_TurnsUnknownIntoOffline_AfterTheThresholdOfFailures()
    {
        var gate = NewGate();
        await using var rig = new CoreRig(c =>
        {
            c.HealthCheckInterval = 20;
            c.OfflineThreshold = 2;
        }, HealthUrl, handler =>
        {
            handler.EnqueueAsync(async (request, token) =>
            {
                await gate.Task;
                throw new HttpRequestException("connection refused");
            });
            for (int i = 0; i < Repeats; i++)
                handler.EnqueueException(new HttpRequestException("connection refused"));
        });

        gate.SetResult(true);
        await rig.WaitForChangeAsync();

        Assert.Equal(WebApiAvailability.Offline, rig.Core.Availability);
        Assert.Equal(CommErrorKind.ConnectionClosed, rig.Changes.Single().Reason);
    }

    [Fact]
    public async Task Probe_RunsEveryInterval()
    {
        var probed = new SemaphoreSlim(0);
        await using var rig = new CoreRig(c => c.HealthCheckInterval = 20, HealthUrl, handler =>
        {
            for (int i = 0; i < Repeats; i++)
            {
                handler.EnqueueAsync((request, token) =>
                {
                    probed.Release();
                    return Task.FromResult(new HttpResponseMessage());
                });
            }
        });

        for (int i = 0; i < 3; i++)
            Assert.True(await probed.WaitAsync(WaitLimit));
    }

    [Fact]
    public async Task Probe_DoesNotRetryAndRaisesNoRequestEvents()
    {
        var gate = NewGate();
        await using var rig = new CoreRig(c =>
        {
            c.HealthCheckInterval = LongInterval;
            c.OfflineThreshold = 1;
            c.Retry.RetryCount = 3;
        }, HealthUrl, handler =>
        {
            handler.EnqueueAsync(async (request, token) =>
            {
                await gate.Task;
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            });
            handler.Enqueue(200);
        });

        gate.SetResult(true);
        await rig.WaitForChangeAsync();

        Assert.Equal(WebApiAvailability.Offline, rig.Core.Availability);
        Assert.Single(rig.Handler.Requests);
        Assert.Empty(rig.Sending);
        Assert.Empty(rig.Completed);
    }

    [Fact]
    public async Task Probe_GoesThroughTheInterceptorChain()
    {
        var gate = NewGate();
        var spy = new ScriptedInterceptor("spy", null, (context, token) =>
        {
            context.Headers["X-Probe"] = "yes";
            return Task.CompletedTask;
        });
        await using var rig = new CoreRig(c => c.HealthCheckInterval = LongInterval, HealthUrl, handler => GatedResponse(handler, gate, 200),
                                          components => components.Interceptors.Add(spy));

        gate.SetResult(true);
        await rig.WaitForChangeAsync();

        Assert.Equal("yes", rig.Handler.Requests.First().Headers["X-Probe"]);
        Assert.Equal(1, spy.SendingCalls);
        Assert.Equal(1, spy.ReceivedCalls);
    }

    [Fact]
    public async Task Probe_TimesOutLikeAnyOtherAttempt()
    {
        await using var rig = new CoreRig(c =>
        {
            c.HealthCheckInterval = LongInterval;
            c.Timeout = 100;
            c.OfflineThreshold = 1;
        }, HealthUrl, handler => handler.EnqueueHang());

        await rig.WaitForChangeAsync();

        Assert.Equal(WebApiAvailability.Offline, rig.Core.Availability);
        Assert.Equal(CommErrorKind.Timeout, rig.Changes.Single().Reason);
    }

    [Fact]
    public async Task Probes_DoNotConsumeUserCallIds()
    {
        var gate = NewGate();
        await using var rig = new CoreRig(c => c.HealthCheckInterval = LongInterval, HealthUrl, handler =>
        {
            GatedResponse(handler, gate, 200);
            handler.Enqueue(200, "ok");
        });
        gate.SetResult(true);
        await rig.WaitForChangeAsync();

        await rig.SendAsync();

        Assert.Equal(1, rig.Completed.Single().CallId);
    }

    [Fact]
    public async Task Dispose_StopsTheProbes()
    {
        var probed = new SemaphoreSlim(0);
        var rig = new CoreRig(c => c.HealthCheckInterval = 20, HealthUrl, handler =>
        {
            for (int i = 0; i < Repeats; i++)
            {
                handler.EnqueueAsync((request, token) =>
                {
                    probed.Release();
                    return Task.FromResult(new HttpResponseMessage());
                });
            }
        });
        Assert.True(await probed.WaitAsync(WaitLimit));

        await rig.DisposeAsync();

        int afterDispose = rig.Handler.Requests.Count;
        await Task.Delay(150);
        Assert.Equal(afterDispose, rig.Handler.Requests.Count);
    }

    private static TaskCompletionSource<bool> NewGate() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    // 第一次探测等到闸门打开才返回；之后的探测立即返回同样的状态码。
    private static void GatedResponse(StubHttpHandler handler, TaskCompletionSource<bool> gate, int status)
    {
        handler.EnqueueAsync(async (request, token) =>
        {
            await gate.Task;
            return new HttpResponseMessage((System.Net.HttpStatusCode)status);
        });
        for (int i = 0; i < Repeats; i++)
            handler.Enqueue(status);
    }
}
