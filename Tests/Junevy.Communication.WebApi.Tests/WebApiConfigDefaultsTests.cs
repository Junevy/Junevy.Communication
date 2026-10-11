namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 共享配置类型的默认值测试：默认值是公开契约（计划 Task 3），改动即为破坏性变更。
/// </summary>
public sealed class WebApiConfigDefaultsTests
{
    [Fact]
    public void EndpointConfig_Defaults()
    {
        var config = new TestEndpointConfig();

        Assert.Equal(10000, config.Timeout);
        Assert.Equal(30000, config.HealthCheckInterval);
        Assert.Equal(3, config.OfflineThreshold);
        Assert.Equal(4194304, config.MaxResponseBodySize);
        Assert.Equal(10, config.MaxConnectionsPerServer);
        Assert.Equal("Idempotency-Key", config.IdempotencyHeaderName);
        Assert.Empty(config.DefaultHeaders);
        Assert.NotNull(config.Auth);
        Assert.NotNull(config.Retry);
        Assert.NotNull(config.Logging);
    }

    [Fact]
    public void RetryOptions_Defaults_DoNotRetry()
    {
        var options = new WebApiRetryOptions();

        Assert.Equal(0, options.RetryCount);
        Assert.Equal(1000, options.RetryDelay);
        Assert.False(options.RetryUnsafeMethods);
    }

    [Fact]
    public void LogOptions_Defaults_RedactCredentialsAndHideBodies()
    {
        var options = new WebApiLogOptions();

        Assert.False(options.LogBodies);
        Assert.Equal(2048, options.MaxLoggedBodyLength);
        Assert.Equal(new[] { "Authorization", "Proxy-Authorization" }, options.RedactedHeaders);
        Assert.Equal(new[] { "access_token", "token", "api_key", "apikey" }, options.RedactedQueryParameters);
    }

    [Fact]
    public void AuthOptions_Defaults_UseNoAuthentication()
    {
        var options = new WebApiAuthOptions();

        Assert.Equal(WebApiAuthScheme.None, options.Scheme);
        Assert.Null(options.UserNameVariable);
        Assert.Null(options.PasswordVariable);
        Assert.Null(options.TokenVariable);
        Assert.Equal("X-API-Key", options.ApiKeyHeader);
        Assert.Null(options.ApiKeyVariable);
    }

    [Fact]
    public void EnvelopeOptions_Defaults_AreDisabled()
    {
        var options = new ResponseEnvelopeOptions();

        Assert.False(options.Enabled);
        Assert.Equal("code", options.CodePath);
        Assert.Equal("msg", options.MessagePath);
        Assert.Equal("data", options.DataPath);
        Assert.Equal(new[] { "0" }, options.SuccessCodes);
    }

    [Fact]
    public void AuthScheme_NumericValuesAreStable()
    {
        Assert.Equal(0, (int)WebApiAuthScheme.None);
        Assert.Equal(1, (int)WebApiAuthScheme.Basic);
        Assert.Equal(2, (int)WebApiAuthScheme.Bearer);
        Assert.Equal(3, (int)WebApiAuthScheme.ApiKey);
    }
}
