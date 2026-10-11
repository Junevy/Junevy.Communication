using Junevy.Communication.WebApi.Rest;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// REST 配置校验与快照（<see cref="RestConfigRules"/>，P3-D16）：BaseUrl、健康检查路径、业务包络；共有字段的规则仍由 <c>EndpointConfigRules</c> 把关。
/// </summary>
public sealed class RestConfigRulesTests
{
    public static IEnumerable<object[]> InvalidCases()
    {
        yield return Case("BaseUrl", c => c.BaseUrl = "");
        yield return Case("BaseUrl", c => c.BaseUrl = "   ");
        yield return Case("BaseUrl", c => c.BaseUrl = null!);
        yield return Case("BaseUrl", c => c.BaseUrl = "mes.local/api");
        yield return Case("BaseUrl", c => c.BaseUrl = "/api");
        yield return Case("BaseUrl", c => c.BaseUrl = "ftp://mes.local/api");
        yield return Case("BaseUrl", c => c.BaseUrl = "http://mes.local/api?key=1");
        yield return Case("BaseUrl", c => c.BaseUrl = "http://mes.local/api#top");
        yield return Case("HealthCheckPath", c => c.HealthCheckPath = "http://evil.local/health");
        yield return Case("HealthCheckPath", c => c.HealthCheckPath = "//evil.local/health");
        yield return Case("Envelope", c => c.Envelope = null!);
        yield return Case("Envelope.CodePath", c => c.Envelope = Enabled(o => o.CodePath = ""));
        yield return Case("Envelope.CodePath", c => c.Envelope = Enabled(o => o.CodePath = null!));
        yield return Case("Envelope.CodePath", c => c.Envelope = Enabled(o => o.CodePath = "result..code"));
        yield return Case("Envelope.MessagePath", c => c.Envelope = Enabled(o => o.MessagePath = ".msg"));
        yield return Case("Envelope.MessagePath", c => c.Envelope = Enabled(o => o.MessagePath = " "));
        yield return Case("Envelope.DataPath", c => c.Envelope = Enabled(o => o.DataPath = "result."));
        yield return Case("Envelope.SuccessCodes", c => c.Envelope = Enabled(o => o.SuccessCodes = null!));
        yield return Case("Envelope.SuccessCodes", c => c.Envelope = Enabled(o => o.SuccessCodes = Array.Empty<string>()));
        yield return Case("Envelope.SuccessCodes", c => c.Envelope = Enabled(o => o.SuccessCodes = new[] { "0", "" }));
        yield return Case("Envelope.SuccessCodes", c => c.Envelope = Enabled(o => o.SuccessCodes = new string[] { null! }));
        yield return Case("Timeout", c => c.Timeout = 0);
        yield return Case("Retry.RetryCount", c => c.Retry.RetryCount = -1);
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Create_InvalidField_ThrowsArgumentExceptionNamingTheField(string field, Action<WebApiClientConfig> mutate)
    {
        WebApiClientConfig config = Valid();
        mutate(config);

        var exception = Assert.ThrowsAny<ArgumentException>(() => RestConfigRules.Create(config));

        Assert.Equal(field, exception.ParamName);
        Assert.Contains(field, exception.Message);
    }

    [Fact]
    public void Create_NullConfig_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => RestConfigRules.Create(null!));

        Assert.Equal("config", exception.ParamName);
    }

    [Theory]
    [InlineData("http://mes.local/api", "http://mes.local/api/")]
    [InlineData("http://mes.local/api/", "http://mes.local/api/")]
    [InlineData("https://mes.local", "https://mes.local/")]
    [InlineData("http://mes.local:8080/a/b", "http://mes.local:8080/a/b/")]
    [InlineData("  http://mes.local/api  ", "http://mes.local/api/")]
    public void Create_NormalizesTheBaseAddressToEndWithASlash(string baseUrl, string expected)
    {
        RestSettings settings = RestConfigRules.Create(Valid(baseUrl));

        Assert.Equal(expected, settings.BaseAddress.AbsoluteUri);
    }

    [Theory]
    [InlineData("health", "http://mes.local/api/health")]
    [InlineData("/health", "http://mes.local/api/health")]
    [InlineData("status?deep=1", "http://mes.local/api/status?deep=1")]
    public void Create_ResolvesTheHealthCheckPathUnderTheBaseAddress(string path, string expected)
    {
        WebApiClientConfig config = Valid();
        config.HealthCheckPath = path;

        Assert.Equal(expected, RestConfigRules.Create(config).HealthCheckUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Create_WithoutAHealthCheckPath_HasNoHealthCheckUri(string? path)
    {
        WebApiClientConfig config = Valid();
        config.HealthCheckPath = path;

        Assert.Null(RestConfigRules.Create(config).HealthCheckUri);
    }

    [Fact]
    public void Create_DisabledEnvelope_IsNotValidated()
    {
        WebApiClientConfig config = Valid();
        config.Envelope = new ResponseEnvelopeOptions { Enabled = false, CodePath = "", SuccessCodes = null! };

        Assert.False(RestConfigRules.Create(config).Envelope.Enabled);
    }

    [Fact]
    public void Create_IncludesTheSharedEndpointSettings()
    {
        WebApiClientConfig config = Valid();
        config.Timeout = 1234;

        Assert.Equal(1234, RestConfigRules.Create(config).Endpoint.Timeout);
    }

    [Fact]
    public void Create_EnvelopeSnapshotIsIsolatedFromLaterChanges()
    {
        WebApiClientConfig config = Valid();
        config.Envelope = Enabled(o =>
        {
            o.CodePath = "result.code";
            o.SuccessCodes = new[] { "0", "200" };
        });
        RestSettings settings = RestConfigRules.Create(config);

        config.Envelope.CodePath = "other";
        config.Envelope.SuccessCodes[0] = "changed";
        config.Envelope.Enabled = false;

        Assert.True(settings.Envelope.Enabled);
        Assert.Equal(new[] { "result", "code" }, settings.Envelope.CodePath);
        Assert.Contains("0", settings.Envelope.SuccessCodes);
        Assert.DoesNotContain("changed", settings.Envelope.SuccessCodes);
    }

    private static WebApiClientConfig Valid(string baseUrl = "http://mes.local/api") => new WebApiClientConfig { BaseUrl = baseUrl };

    private static ResponseEnvelopeOptions Enabled(Action<ResponseEnvelopeOptions> configure)
    {
        var options = new ResponseEnvelopeOptions { Enabled = true };
        configure(options);
        return options;
    }

    private static object[] Case(string field, Action<WebApiClientConfig> mutate) => new object[] { field, mutate };
}
