using System.Net;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 认证与拦截器的端到端行为（P32-D9、D10）：真实 Kestrel 上的 401 与 WWW-Authenticate、请求阶段的顺序与拒绝、响应阶段的逆序与响应头、异常转 500。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class InterceptorTests
{
    [Fact]
    public async Task BuiltInBasic_Failure_IsA401WithAChallenge_AndNeverEchoesTheSecret()
    {
        using var variables = new TemporaryVariables();
        string user = variables.Set("alice");
        string password = variables.Set("s3cret-value");
        bool handlerRan = false;
        WebApiServerSettings settings = SettingsFactory.Create(config => config.Auth = new WebApiAuthOptions
        {
            Scheme = WebApiAuthScheme.Basic,
            UserNameVariable = user,
            PasswordVariable = password,
        });
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/secure", (_, _) =>
            {
                handlerRan = true;
                return Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()));
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/secure");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Basic realm=\"test\"", response.Headers.WwwAuthenticate.ToString());
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cret-value", body);
        Assert.False(handlerRan);
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);
        Assert.Equal(WebApiServerOutcome.Rejected, server.Completed[0].Outcome);
        Assert.Equal(401, server.Completed[0].StatusCode);
    }

    [Fact]
    public async Task BuiltInBasic_Success_ExposesTheUserNameToTheHandler()
    {
        using var variables = new TemporaryVariables();
        string user = variables.Set("alice");
        string password = variables.Set("s3cret-value");
        WebApiServerSettings settings = SettingsFactory.Create(config => config.Auth = new WebApiAuthOptions
        {
            Scheme = WebApiAuthScheme.Basic,
            UserNameVariable = user,
            PasswordVariable = password,
        });
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/whoami", (context, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order { Name = context.UserName ?? "none" }))),
        });
        await server.StartAsync();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Authorization", "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("alice:s3cret-value")));

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/whoami");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"alice\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CustomAuthenticator_DecidesInsteadOfTheConfiguredScheme()
    {
        var authenticator = new DenyAll();
        WebApiServerSettings settings = SettingsFactory.Create(null, new WebApiServerComponents { Authenticator = authenticator });
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("WWW-Authenticate"));
    }

    [Fact]
    public async Task AuthenticatorException_IsA500_AndIsLogged()
    {
        WebApiServerSettings settings = SettingsFactory.Create(null, new WebApiServerComponents { Authenticator = new Throwing() });
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/x");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);
        Assert.Equal(WebApiServerOutcome.HandlerFailed, server.Completed[0].Outcome);
        Assert.Contains(server.Logger.GetEntries(LogLevel.Error), entry => entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task RequestInterceptors_RunInRegistrationOrder_AndResponseInterceptorsRunInReverse()
    {
        var order = new List<string>();
        var components = new WebApiServerComponents();
        components.Interceptors.Add(new Recorder("first", order));
        components.Interceptors.Add(new Recorder("second", order));
        WebApiServerSettings settings = SettingsFactory.Create(null, components);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/x", (_, _) =>
            {
                lock (order)
                    order.Add("handler");
                return Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()));
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        await http.GetAsync(server.BaseUrl + "/x");

        lock (order)
        {
            Assert.Equal(new[] { "request first", "request second", "handler", "respond second", "respond first" }, order);
        }
    }

    [Fact]
    public async Task RequestInterceptorReject_StopsTheChainAndTheHandler_AndIsReportedAsRejected()
    {
        var order = new List<string>();
        var components = new WebApiServerComponents();
        components.Interceptors.Add(new Rejecting(order));
        components.Interceptors.Add(new Recorder("after", order));
        WebApiServerSettings settings = SettingsFactory.Create(config => config.Envelope = SettingsFactory.NestedEnvelope(), components);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/x", (_, _) =>
            {
                lock (order)
                    order.Add("handler");
                return Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()));
            }),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/x");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("E403", await response.Content.ReadAsStringAsync());
        await Polling.WaitUntilAsync(() => server.Completed.Count == 1);
        Assert.Equal(WebApiServerOutcome.Rejected, server.Completed[0].Outcome);
        Assert.Equal("E403", server.Completed[0].Code);
        lock (order)
            Assert.Equal(new[] { "reject", "respond rejecting" }, order);
    }

    [Fact]
    public async Task ResponseInterceptor_CanAddHeaders_AndSeesTheOutcome()
    {
        var components = new WebApiServerComponents();
        components.Interceptors.Add(new TraceHeader());
        WebApiServerSettings settings = SettingsFactory.Create(null, components);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/x");

        Assert.Equal("succeeded", response.Headers.GetValues("X-Outcome").Single());
    }

    [Fact]
    public async Task RequestInterceptorException_IsA500_AndIsLoggedAsAnError()
    {
        var components = new WebApiServerComponents();
        components.Interceptors.Add(new ThrowingInterceptor());
        WebApiServerSettings settings = SettingsFactory.Create(null, components);
        await using var server = new ServerUnderTest(settings, new[]
        {
            TestRoutes.Get<Order>(settings, "/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))),
        });
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage response = await http.GetAsync(server.BaseUrl + "/x");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(server.Logger.GetEntries(LogLevel.Error), entry => entry.Exception is InvalidOperationException);
    }

    private sealed class DenyAll : IWebApiRequestAuthenticator
    {
        public Task<WebApiAuthenticationResult> AuthenticateAsync(WebApiServerContext context, CancellationToken cancellationToken)
            => Task.FromResult(WebApiAuthenticationResult.Fail("denied by the custom authenticator"));
    }

    private sealed class Throwing : IWebApiRequestAuthenticator
    {
        public Task<WebApiAuthenticationResult> AuthenticateAsync(WebApiServerContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("authenticator failure");
    }

    private sealed class Recorder : WebApiServerInterceptor
    {
        private readonly string name;
        private readonly List<string> order;

        public Recorder(string name, List<string> order)
        {
            this.name = name;
            this.order = order;
        }

        public override Task OnRequestAsync(WebApiServerRequestingContext context, CancellationToken cancellationToken)
        {
            lock (order)
                order.Add("request " + name);
            return Task.CompletedTask;
        }

        public override Task OnRespondingAsync(WebApiServerRespondingContext context, CancellationToken cancellationToken)
        {
            lock (order)
                order.Add("respond " + name);
            return Task.CompletedTask;
        }
    }

    private sealed class Rejecting : WebApiServerInterceptor
    {
        private readonly List<string> order;

        public Rejecting(List<string> order)
        {
            this.order = order;
        }

        public override Task OnRequestAsync(WebApiServerRequestingContext context, CancellationToken cancellationToken)
        {
            lock (order)
                order.Add("reject");
            context.Reject(403, "E403", "forbidden by policy");
            return Task.CompletedTask;
        }

        public override Task OnRespondingAsync(WebApiServerRespondingContext context, CancellationToken cancellationToken)
        {
            lock (order)
                order.Add("respond rejecting");
            return Task.CompletedTask;
        }
    }

    private sealed class TraceHeader : WebApiServerInterceptor
    {
        public override Task OnRespondingAsync(WebApiServerRespondingContext context, CancellationToken cancellationToken)
        {
            context.Headers["X-Outcome"] = context.Outcome == WebApiServerOutcome.Succeeded ? "succeeded" : "other";
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingInterceptor : WebApiServerInterceptor
    {
        public override Task OnRequestAsync(WebApiServerRequestingContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("interceptor failure");
    }
}
