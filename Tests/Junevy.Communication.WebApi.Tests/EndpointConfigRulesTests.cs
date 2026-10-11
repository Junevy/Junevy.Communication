using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="EndpointConfigRules"/> 的校验与快照测试（P3-D16）：每个非法字段一条用例，快照与调用方的配置对象隔离。
/// </summary>
public sealed class EndpointConfigRulesTests
{
    public static IEnumerable<object[]> InvalidCases()
    {
        yield return Case("Timeout", c => c.Timeout = 0);
        yield return Case("Timeout", c => c.Timeout = -1);
        yield return Case("HealthCheckInterval", c => c.HealthCheckInterval = 0);
        yield return Case("OfflineThreshold", c => c.OfflineThreshold = 0);
        yield return Case("MaxResponseBodySize", c => c.MaxResponseBodySize = 0);
        yield return Case("MaxConnectionsPerServer", c => c.MaxConnectionsPerServer = 0);
        yield return Case("IdempotencyHeaderName", c => c.IdempotencyHeaderName = "  ");
        yield return Case("Retry", c => c.Retry = null!);
        yield return Case("Retry.RetryCount", c => c.Retry.RetryCount = -1);
        yield return Case("Retry.RetryDelay", c => c.Retry.RetryDelay = -1);
        yield return Case("Logging", c => c.Logging = null!);
        yield return Case("Logging.MaxLoggedBodyLength", c => c.Logging.MaxLoggedBodyLength = -1);
        yield return Case("Logging.RedactedHeaders", c => c.Logging.RedactedHeaders = null!);
        yield return Case("Logging.RedactedHeaders", c => c.Logging.RedactedHeaders = new[] { "Authorization", "" });
        yield return Case("Logging.RedactedQueryParameters", c => c.Logging.RedactedQueryParameters = null!);
        yield return Case("Logging.RedactedQueryParameters", c => c.Logging.RedactedQueryParameters = new string[] { null! });
        yield return Case("DefaultHeaders", c => c.DefaultHeaders = null!);
        yield return Case("DefaultHeaders", c => c.DefaultHeaders = new Dictionary<string, string> { [""] = "value" });
        yield return Case("DefaultHeaders", c => c.DefaultHeaders = new Dictionary<string, string> { [" "] = "value" });
        yield return Case("DefaultHeaders", c => c.DefaultHeaders = new Dictionary<string, string> { ["X-Trace"] = null! });
        yield return Case("DefaultHeaders", c => c.DefaultHeaders = new Dictionary<string, string> { ["X-Trace"] = "1", ["x-trace"] = "2" });
        yield return Case("Auth", c => c.Auth = null!);
        yield return Case("Auth.Scheme", c => c.Auth.Scheme = (WebApiAuthScheme)99);
        yield return Case("Auth.UserNameVariable", c => c.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Basic, PasswordVariable = "P" });
        yield return Case("Auth.PasswordVariable", c => c.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Basic, UserNameVariable = "U" });
        yield return Case("Auth.TokenVariable", c => c.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Bearer });
        yield return Case("Auth.ApiKeyHeader", c => c.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.ApiKey, ApiKeyHeader = "", ApiKeyVariable = "K" });
        yield return Case("Auth.ApiKeyVariable", c => c.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.ApiKey });
    }

    public static IEnumerable<object[]> ValidCases()
    {
        yield return Valid(c => c.Timeout = 1);
        yield return Valid(c => c.OfflineThreshold = 1);
        yield return Valid(c => c.Retry = new WebApiRetryOptions { RetryCount = 0, RetryDelay = 0 });
        yield return Valid(c => c.Logging.MaxLoggedBodyLength = 0);
        yield return Valid(c => c.Logging.RedactedHeaders = Array.Empty<string>());
        yield return Valid(c => c.IdempotencyHeaderName = null);
        yield return Valid(c => c.IdempotencyHeaderName = "");
        yield return Valid(c => c.DefaultHeaders = new Dictionary<string, string> { ["X-Trace"] = "", ["Accept-Language"] = "zh-CN" });
        yield return Valid(c => c.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Basic, UserNameVariable = "U", PasswordVariable = "P" });
        yield return Valid(c => c.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Bearer, TokenVariable = "T" });
        yield return Valid(c => c.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.ApiKey, ApiKeyVariable = "K" });
        // 认证方式为 None 时，其余认证字段不检查。
        yield return Valid(c => c.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.None, ApiKeyHeader = "" });
    }

    [Fact]
    public void CreateSettings_NullConfig_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => EndpointConfigRules.CreateSettings(null!));

        Assert.Equal("config", exception.ParamName);
    }

    [Fact]
    public void CreateSettings_DefaultConfig_IsValidAndCopiesTheDefaults()
    {
        EndpointSettings settings = EndpointConfigRules.CreateSettings(new TestEndpointConfig());

        Assert.Equal(10000, settings.Timeout);
        Assert.Equal(30000, settings.HealthCheckInterval);
        Assert.Equal(3, settings.OfflineThreshold);
        Assert.Equal(4194304, settings.MaxResponseBodySize);
        Assert.Equal(10, settings.MaxConnectionsPerServer);
        Assert.Equal("Idempotency-Key", settings.IdempotencyHeaderName);
        Assert.Empty(settings.DefaultHeaders);
        Assert.Equal(WebApiAuthScheme.None, settings.Auth.Scheme);
        Assert.Equal(0, settings.RetryCount);
        Assert.Equal(1000, settings.RetryDelay);
        Assert.False(settings.RetryUnsafeMethods);
        Assert.False(settings.LogBodies);
        Assert.Equal(2048, settings.MaxLoggedBodyLength);
        Assert.Equal(new[] { "Authorization", "Proxy-Authorization" }, settings.RedactedHeaders);
        Assert.Equal(new[] { "access_token", "token", "api_key", "apikey" }, settings.RedactedQueryParameters);
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void CreateSettings_InvalidField_ThrowsArgumentExceptionNamingTheField(string field, Action<TestEndpointConfig> mutate)
    {
        var config = new TestEndpointConfig();
        mutate(config);

        var exception = Assert.ThrowsAny<ArgumentException>(() => EndpointConfigRules.CreateSettings(config));

        Assert.Equal(field, exception.ParamName);
        Assert.Contains(field, exception.Message);
    }

    [Theory]
    [MemberData(nameof(ValidCases))]
    public void CreateSettings_ValidVariation_Succeeds(Action<TestEndpointConfig> mutate)
    {
        var config = new TestEndpointConfig();
        mutate(config);

        Assert.NotNull(EndpointConfigRules.CreateSettings(config));
    }

    [Fact]
    public void CreateSettings_EmptyIdempotencyHeaderName_MeansNoHeader()
    {
        var config = new TestEndpointConfig { IdempotencyHeaderName = "" };

        Assert.Null(EndpointConfigRules.CreateSettings(config).IdempotencyHeaderName);
    }

    [Fact]
    public void CreateSettings_DefaultHeaders_AreCaseInsensitive()
    {
        var config = new TestEndpointConfig { DefaultHeaders = new Dictionary<string, string> { ["X-Trace"] = "abc" } };

        EndpointSettings settings = EndpointConfigRules.CreateSettings(config);

        Assert.Equal("abc", settings.DefaultHeaders["x-trace"]);
    }

    [Fact]
    public void CreateSettings_ValidationDoesNotModifyTheCallerConfig()
    {
        var config = new TestEndpointConfig { IdempotencyHeaderName = "" };

        EndpointConfigRules.CreateSettings(config);

        Assert.Equal("", config.IdempotencyHeaderName);
    }

    [Fact]
    public void CreateSettings_SnapshotIsIsolatedFromLaterChanges()
    {
        var config = new TestEndpointConfig
        {
            DefaultHeaders = new Dictionary<string, string> { ["X-Trace"] = "abc" },
            Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Bearer, TokenVariable = "TOKEN_VAR" },
            Retry = new WebApiRetryOptions { RetryCount = 2, RetryDelay = 50, RetryUnsafeMethods = true },
            Logging = new WebApiLogOptions { LogBodies = true, MaxLoggedBodyLength = 10 },
        };
        EndpointSettings settings = EndpointConfigRules.CreateSettings(config);

        config.Timeout = 1;
        config.HealthCheckInterval = 1;
        config.OfflineThreshold = 1;
        config.MaxResponseBodySize = 1;
        config.MaxConnectionsPerServer = 1;
        config.IdempotencyHeaderName = "Other";
        config.DefaultHeaders["X-Trace"] = "changed";
        config.DefaultHeaders["X-New"] = "added";
        config.Auth.Scheme = WebApiAuthScheme.None;
        config.Auth.TokenVariable = "OTHER_VAR";
        config.Retry.RetryCount = 9;
        config.Retry.RetryDelay = 9;
        config.Retry.RetryUnsafeMethods = false;
        config.Logging.LogBodies = false;
        config.Logging.MaxLoggedBodyLength = 99;
        config.Logging.RedactedHeaders[0] = "Changed";
        config.Logging.RedactedQueryParameters[0] = "changed";

        Assert.Equal(10000, settings.Timeout);
        Assert.Equal(30000, settings.HealthCheckInterval);
        Assert.Equal(3, settings.OfflineThreshold);
        Assert.Equal(4194304, settings.MaxResponseBodySize);
        Assert.Equal(10, settings.MaxConnectionsPerServer);
        Assert.Equal("Idempotency-Key", settings.IdempotencyHeaderName);
        Assert.Equal("abc", settings.DefaultHeaders["X-Trace"]);
        Assert.False(settings.DefaultHeaders.ContainsKey("X-New"));
        Assert.Equal(WebApiAuthScheme.Bearer, settings.Auth.Scheme);
        Assert.Equal("TOKEN_VAR", settings.Auth.TokenVariable);
        Assert.Equal(2, settings.RetryCount);
        Assert.Equal(50, settings.RetryDelay);
        Assert.True(settings.RetryUnsafeMethods);
        Assert.True(settings.LogBodies);
        Assert.Equal(10, settings.MaxLoggedBodyLength);
        Assert.Equal("Authorization", settings.RedactedHeaders[0]);
        Assert.Equal("access_token", settings.RedactedQueryParameters[0]);
    }

    private static object[] Case(string field, Action<TestEndpointConfig> mutate) => new object[] { field, mutate };

    private static object[] Valid(Action<TestEndpointConfig> mutate) => new object[] { mutate };
}
