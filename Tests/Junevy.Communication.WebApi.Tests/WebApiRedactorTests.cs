using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="WebApiRedactor"/> 的测试（P3-D17）：配置的头与查询参数、拦截器标记的头，值一律替换为 <c>***</c>。
/// </summary>
public sealed class WebApiRedactorTests
{
    private readonly WebApiRedactor redactor = new WebApiRedactor(EndpointConfigRules.CreateSettings(new TestEndpointConfig()));

    [Fact]
    public void RedactHeaders_ConfiguredNames_AreMaskedCaseInsensitively()
    {
        var headers = new Dictionary<string, string> { ["authorization"] = "Bearer secret", ["Proxy-Authorization"] = "Basic x", ["Accept"] = "application/json" };

        IReadOnlyDictionary<string, string> redacted = redactor.RedactHeaders(headers, Array.Empty<string>());

        Assert.Equal("***", redacted["Authorization"]);
        Assert.Equal("***", redacted["Proxy-Authorization"]);
        Assert.Equal("application/json", redacted["Accept"]);
    }

    [Fact]
    public void RedactHeaders_InterceptorMarkedNames_AreMasked()
    {
        var headers = new Dictionary<string, string> { ["X-Signature"] = "abc123", ["X-Trace"] = "t-1" };

        IReadOnlyDictionary<string, string> redacted = redactor.RedactHeaders(headers, new[] { "x-signature" });

        Assert.Equal("***", redacted["X-Signature"]);
        Assert.Equal("t-1", redacted["X-Trace"]);
    }

    [Fact]
    public void RedactHeaders_DoesNotModifyTheSuppliedHeaders()
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer secret" };

        redactor.RedactHeaders(headers, Array.Empty<string>());

        Assert.Equal("Bearer secret", headers["Authorization"]);
    }

    [Theory]
    [InlineData("http://mes.local/api?access_token=secret&page=2", "http://mes.local/api?access_token=***&page=2")]
    [InlineData("http://mes.local/api?page=2&Token=secret", "http://mes.local/api?page=2&Token=***")]
    [InlineData("http://mes.local/api?API_KEY=a&apikey=b&other=c", "http://mes.local/api?API_KEY=***&apikey=***&other=c")]
    [InlineData("http://mes.local/api?name=%E5%B7%A5%E5%8D%95&token=s", "http://mes.local/api?name=%E5%B7%A5%E5%8D%95&token=***")]
    [InlineData("http://mes.local/api?flag&token=s", "http://mes.local/api?flag&token=***")]
    [InlineData("http://mes.local/api?token=", "http://mes.local/api?token=***")]
    public void RedactUri_MasksTheValuesOfConfiguredQueryParameters(string uri, string expected)
    {
        Assert.Equal(expected, redactor.RedactUri(new Uri(uri)).AbsoluteUri);
    }

    [Theory]
    [InlineData("http://mes.local/api/orders")]
    [InlineData("http://mes.local/api/orders?page=2&size=10")]
    public void RedactUri_WithoutSensitiveParameters_ReturnsTheSameUri(string uri)
    {
        Assert.Equal(new Uri(uri), redactor.RedactUri(new Uri(uri)));
    }
}
