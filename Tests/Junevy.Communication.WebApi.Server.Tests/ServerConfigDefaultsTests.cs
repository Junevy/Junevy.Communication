using Junevy.Communication.WebApi.Server.Config;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 配置默认值逐项断言（P32 Task 1 第 3 条）：默认值是对外承诺，改动默认值必须同时改计划与文档。
/// </summary>
public sealed class ServerConfigDefaultsTests
{
    [Fact]
    public void WebApiServerConfig_DefaultsMatchThePlan()
    {
        var config = new WebApiServerConfig();

        Assert.Equal(new[] { "http://*:8080" }, config.Urls);
        Assert.NotNull(config.Https);
        Assert.Equal(WebApiAuthScheme.None, config.Auth.Scheme);
        Assert.Equal(16, config.MaxConcurrentRequests);
        Assert.Equal(30000, config.RequestTimeout);
        Assert.Equal(1048576L, config.MaxRequestBodySize);
        Assert.Equal(5000, config.StopTimeout);
        Assert.False(config.Envelope.Enabled);
        Assert.Equal(200, config.BusinessFailureStatusCode);
        Assert.False(config.Deduplication.Enabled);
        Assert.False(config.Logging.LogBodies);
    }

    [Fact]
    public void WebApiServerHttpsOptions_DefaultsHaveNoCertificateSource()
    {
        var https = new WebApiServerHttpsOptions();

        Assert.Null(https.CertificatePath);
        Assert.Null(https.CertificatePasswordVariable);
    }

    [Fact]
    public void WebApiServerDeduplicationOptions_DefaultsMatchThePlan()
    {
        var deduplication = new WebApiServerDeduplicationOptions();

        Assert.False(deduplication.Enabled);
        Assert.Equal("Idempotency-Key", deduplication.HeaderName);
        Assert.Equal(600000, deduplication.Window);
        Assert.Equal(1000, deduplication.MaxEntries);
    }

    [Fact]
    public void WebApiServerComponents_DefaultsAreEmptyAndUseTheBuiltIns()
    {
        var components = new WebApiServerComponents();

        Assert.Empty(components.Interceptors);
        Assert.Null(components.Serializer);
        Assert.Null(components.Authenticator);
        Assert.Null(components.Certificate);
        Assert.Null(components.TimeProvider);
    }

    [Fact]
    public void WebApiServerState_HasTheFourLifecycleStates()
    {
        Assert.Equal(
            new[] { WebApiServerState.Stopped, WebApiServerState.Starting, WebApiServerState.Running, WebApiServerState.Stopping },
            Enum.GetValues<WebApiServerState>());
    }
}
