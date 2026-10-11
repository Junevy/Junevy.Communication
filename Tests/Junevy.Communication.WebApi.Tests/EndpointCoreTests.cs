using System.Net.Http;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Http;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 共享端点核心（计划 Task 7 第 3 条）：组装、在线程池上进入管线、可用性事件的顺序、释放的各条规则。
/// </summary>
public sealed class EndpointCoreTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly string authVariable = "JV_TEST_CORE_TOKEN_" + Guid.NewGuid().ToString("N");

    public void Dispose() => Environment.SetEnvironmentVariable(authVariable, null);

    [Fact]
    public async Task Execute_RunsTheWholePipeline()
    {
        await using var rig = new CoreRig(c => c.Retry.RetryCount = 1, script: handler => handler.Enqueue(503).Enqueue(200, "ok"));

        WebApiResult<string> result = await rig.SendAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", result.Data);
        Assert.Equal(2, rig.Handler.Requests.Count);
        WebApiCompletedEventArgs completed = Assert.Single(rig.Completed);
        Assert.Equal(2, completed.Attempts);
    }

    [Fact]
    public async Task Execute_RunsInterceptorsAndEventsOnThePool_EvenWhenCalledFromADedicatedThread()
    {
        bool? interceptorOnPool = null;
        bool? eventOnPool = null;
        var spy = ScriptedInterceptor.Sending("spy", context => interceptorOnPool = Thread.CurrentThread.IsThreadPoolThread);
        await using var rig = new CoreRig(script: handler => handler.Enqueue(200), configureComponents: components => components.Interceptors.Add(spy));
        rig.Core.Events.RequestCompleted += (sender, args) => eventOnPool = Thread.CurrentThread.IsThreadPoolThread;
        Task<WebApiResult<string>>? call = null;

        var caller = new Thread(() => call = rig.SendAsync());
        caller.Start();
        caller.Join();
        await call!.WaitAsyncCompat(WaitLimit);

        Assert.Equal(true, interceptorOnPool);
        Assert.Equal(true, eventOnPool);
    }

    [Fact]
    public async Task ConcurrentCalls_GetDistinctCallIds()
    {
        const int calls = 20;
        await using var rig = new CoreRig(script: handler =>
        {
            for (int i = 0; i < calls; i++)
                handler.Enqueue(200);
        });

        await Task.WhenAll(Enumerable.Range(0, calls).Select(_ => rig.SendAsync()));

        Assert.Equal(Enumerable.Range(1, calls).Select(id => (long)id), rig.Completed.Select(args => args.CallId).OrderBy(id => id));
    }

    [Fact]
    public async Task ConfiguredAuthentication_RunsBeforeTheUserInterceptors()
    {
        Environment.SetEnvironmentVariable(authVariable, "env-token");
        var order = new List<string>();
        var user = new ScriptedInterceptor("user", order);
        await using var rig = new CoreRig(c => c.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Bearer, TokenVariable = authVariable },
                                          script: handler => handler.Enqueue(200), configureComponents: components => components.Interceptors.Add(user));

        await rig.SendAsync();

        Assert.Equal("Bearer env-token", rig.Handler.Requests.Single().Headers["Authorization"]);
        Assert.Equal("***", rig.Sending.Single().Headers["Authorization"]);
    }

    [Fact]
    public void ConfiguredAuthentication_WithAMissingVariable_FailsAtConstructionNamingTheVariable()
    {
        var exception = Assert.Throws<ArgumentException>(() => new CoreRig(c =>
            c.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Bearer, TokenVariable = authVariable }));

        Assert.Contains(authVariable, exception.Message);
    }

    [Fact]
    public void Components_WithANullInterceptor_Throw()
    {
        var exception = Assert.Throws<ArgumentException>(() => new CoreRig(configureComponents: components => components.Interceptors.Add(null!)));

        Assert.Equal("components", exception.ParamName);
    }

    [Fact]
    public async Task WithoutAnInjectedHandler_TheCoreCreatesAndReleasesItsOwn()
    {
        var core = new EndpointCore(this, "mes", LoopRig.BaseAddress, null, EndpointConfigRules.CreateSettings(new TestEndpointConfig()), null, new TestLogger());

        await core.DisposeAsync();
        await core.DisposeAsync();
    }

    [Fact]
    public async Task Availability_Changes_AreRaisedInOrderWithTheFinalStateConsistent()
    {
        await using var rig = new CoreRig(c => c.OfflineThreshold = 1, script: handler =>
        {
            for (int i = 0; i < 6; i++)
                handler.EnqueueException(new HttpRequestException("reset")).Enqueue(200);
        });

        for (int i = 0; i < 12; i++)
            await rig.SendAsync();
        for (int i = 0; i < 12; i++)
            await rig.WaitForChangeAsync();

        WebApiAvailability expectedPrevious = WebApiAvailability.Unknown;
        foreach (WebApiAvailabilityChangedEventArgs change in rig.Changes)
        {
            Assert.Equal(expectedPrevious, change.PreviousAvailability);
            expectedPrevious = change.CurrentAvailability;
        }

        Assert.Equal(rig.Core.Availability, expectedPrevious);
        Assert.Equal(WebApiAvailability.Online, expectedPrevious);
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AsyncAndSync()
    {
        var rig = new CoreRig();

        await rig.DisposeAsync();
        await rig.DisposeAsync();
        rig.Core.Dispose();
        rig.Core.Dispose();
    }

    [Fact]
    public async Task Dispose_ConcurrentCalls_AllComplete()
    {
        var rig = new CoreRig();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await rig.DisposeAsync())));
    }

    [Fact]
    public async Task AfterDispose_CallsReturnNotConnected_WithoutEventsOrRequests()
    {
        var rig = new CoreRig(script: handler => handler.Enqueue(200));
        await rig.DisposeAsync();

        WebApiResult<string> result = await rig.SendAsync();
        WebApiResult<string> rejected = rig.Core.Reject<string>("GET", "invalid", CommErrorKind.InvalidRequest);

        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
        Assert.Equal("The WebApi client has been disposed.", result.ErrorMessage);
        Assert.Equal(CommErrorKind.NotConnected, rejected.ErrorKind);
        Assert.Empty(rig.Completed);
        Assert.Empty(rig.Sending);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Dispose_WithARequestInFlight_ReturnsConnectionClosed()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new CoreRig(script: handler => handler.EnqueueAsync(async (request, token) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage();
        }));
        Task<WebApiResult<string>> call = rig.SendAsync();
        await started.Task.WaitAsyncCompat(WaitLimit);

        await rig.DisposeAsync();
        WebApiResult<string> result = await call.WaitAsyncCompat(WaitLimit);

        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Contains("disposed", result.ErrorMessage);
        Assert.Equal(CommErrorKind.ConnectionClosed, Assert.Single(rig.Completed).ErrorKind);
    }

    [Fact]
    public async Task CallerCancellation_StaysCancelled_AndDoesNotDisposeTheCore()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new CoreRig(script: handler => handler.EnqueueAsync(async (request, token) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage();
        }).Enqueue(200));
        using var source = new CancellationTokenSource();
        Task<WebApiResult<string>> call = rig.SendAsync(cancellationToken: source.Token);
        await started.Task.WaitAsyncCompat(WaitLimit);

        source.Cancel();
        WebApiResult<string> result = await call.WaitAsyncCompat(WaitLimit);

        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
        Assert.True((await rig.SendAsync()).IsSuccess);
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeAnInjectedHandler()
    {
        var rig = new CoreRig();

        await rig.DisposeAsync();

        Assert.False(rig.Handler.IsDisposed);
    }

    [Fact]
    public async Task Dispose_DrainsThePendingAvailabilityEventsBeforeReturning()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new ManualResetEventSlim();
        var rig = new CoreRig(c => c.OfflineThreshold = 1, script: handler => handler.EnqueueException(new HttpRequestException("reset")).Enqueue(200));
        rig.Core.Events.AvailabilityChanged += (sender, args) =>
        {
            entered.TrySetResult(true);
            gate.Wait(WaitLimit);
        };
        await rig.SendAsync();
        await entered.Task.WaitAsyncCompat(WaitLimit);
        await rig.SendAsync();

        Task disposing = rig.DisposeAsync().AsTask();
        gate.Set();
        await disposing.WaitAsyncCompat(WaitLimit);

        Assert.Equal(2, rig.Changes.Count);
        Assert.DoesNotContain(rig.Logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("pending availability"));
    }

    [Fact]
    public async Task Dispose_WithAStuckSubscriber_GivesUpAfterTheDrainLimitAndLogsAWarning()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new ManualResetEventSlim();
        var rig = new CoreRig(c => c.OfflineThreshold = 1, script: handler => handler.EnqueueException(new HttpRequestException("reset")).Enqueue(200));
        rig.Core.Events.AvailabilityChanged += (sender, args) =>
        {
            entered.TrySetResult(true);
            gate.Wait(WaitLimit);
        };
        await rig.SendAsync();
        await entered.Task.WaitAsyncCompat(WaitLimit);
        await rig.SendAsync();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await rig.DisposeAsync();
        stopwatch.Stop();
        gate.Set();

        Assert.InRange(stopwatch.ElapsedMilliseconds, EndpointCore.DisposeDrainMilliseconds - 200, EndpointCore.DisposeDrainMilliseconds + 5000);
        TestLogEntry warning = Assert.Single(rig.Logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("pending availability"));
        Assert.Contains("1 pending", warning.Message);
    }

    [Fact]
    public async Task Dispose_FromAnAvailabilityHandler_DoesNotWaitForItself()
    {
        var disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new CoreRig(c => c.OfflineThreshold = 1, script: handler => handler.EnqueueException(new HttpRequestException("reset")));
        rig.Core.Events.AvailabilityChanged += (sender, args) =>
        {
            rig.Core.Dispose();
            disposed.TrySetResult(true);
        };

        await rig.SendAsync();

        await disposed.Task.WaitAsyncCompat(WaitLimit);
    }
}
