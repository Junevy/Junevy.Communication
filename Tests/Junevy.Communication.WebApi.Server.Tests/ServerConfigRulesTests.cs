using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Server.Config;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 服务器配置校验（P32 Task 1 第 2、3 条）：每条规则一个用例，错误的参数名与消息都要检查；快照与调用方的配置对象隔离。
/// 环境变量使用随机名称，并在测试结束时清除。
/// </summary>
public sealed class ServerConfigRulesTests : IDisposable
{
    private readonly List<string> variables = new List<string>();

    public static TheoryData<string> InvalidAddresses => new TheoryData<string>
    {
        "ftp://localhost:21",
        "http://localhost",
        "http://localhost:65536",
        "http://localhost:8080/orders",
        "http://*",
        "http://[::1]",
        "",
        "   ",
    };

    public static TheoryData<string> ValidHttpAddresses => new TheoryData<string>
    {
        "http://*:8080",
        "http://+:8080",
        "http://*:0",
        "http://localhost:8080",
        "http://127.0.0.1:0",
        "http://[::]:8080",
        "http://[::1]:8080/",
    };

    [Fact]
    public void Create_RejectsNullConfig_WithTheParameterName()
    {
        var error = Assert.Throws<ArgumentNullException>(() => WebApiServerConfigRules.Create(null, new WebApiServerComponents()));
        Assert.Equal("config", error.ParamName);
    }

    [Fact]
    public void Create_RejectsNullComponents_WithTheParameterName()
    {
        var error = Assert.Throws<ArgumentNullException>(() => WebApiServerConfigRules.Create(new WebApiServerConfig(), null));
        Assert.Equal("components", error.ParamName);
    }

    [Fact]
    public void Urls_RejectsAnEmptyList()
    {
        var config = new WebApiServerConfig { Urls = Array.Empty<string>() };

        AssertRejected(config, "config.Urls", "at least one address");
    }

    [Theory]
    [MemberData(nameof(InvalidAddresses))]
    public void Urls_RejectsMalformedAddresses(string url)
    {
        var config = new WebApiServerConfig { Urls = new[] { url } };

        AssertRejected(config, "config.Urls");
    }

    [Fact]
    public void Urls_RejectsTheSameAddressTwice_IgnoringCaseAndTrailingSlash()
    {
        var config = new WebApiServerConfig { Urls = new[] { "http://*:8080", "HTTP://*:8080/" } };

        AssertRejected(config, "config.Urls", "more than once");
    }

    [Theory]
    [MemberData(nameof(ValidHttpAddresses))]
    public void Urls_AcceptsKestrelAddressForms(string url)
    {
        var config = new WebApiServerConfig { Urls = new[] { url } };

        WebApiServerSettings settings = WebApiServerConfigRules.Create(config, new WebApiServerComponents());

        Assert.Equal(new[] { url }, settings.Urls);
        Assert.Same(ServerHttpsSettings.NotRequired, settings.Https);
    }

    [Fact]
    public void Https_WithoutAnyCertificateSource_IsRejected()
    {
        var config = new WebApiServerConfig { Urls = new[] { "https://127.0.0.1:0" } };

        AssertRejected(config, "config.Https.CertificatePath", ".pfx");
    }

    [Fact]
    public void Https_UsesTheProvidedCertificate_AndIgnoresThePath()
    {
        using TestCertificate certificate = TestCertificates.CreateSelfSigned();
        var config = new WebApiServerConfig
        {
            Urls = new[] { "https://127.0.0.1:0" },
            Https = new WebApiServerHttpsOptions { CertificatePath = @"Z:\does-not-exist.pfx", CertificatePasswordVariable = "UNSET_VARIABLE" },
        };
        var components = new WebApiServerComponents { Certificate = certificate.Certificate };

        WebApiServerSettings settings = WebApiServerConfigRules.Create(config, components);

        Assert.Same(certificate.Certificate, settings.Https.ProvidedCertificate);
        Assert.Null(settings.Https.CertificatePath);
        Assert.Null(settings.Https.CertificatePassword);
    }

    [Fact]
    public void Https_ReadsTheCertificatePasswordFromTheNamedVariable()
    {
        string variable = SetVariable("pfx-password-value");
        var config = new WebApiServerConfig
        {
            Urls = new[] { "https://127.0.0.1:0" },
            Https = new WebApiServerHttpsOptions { CertificatePath = "server.pfx", CertificatePasswordVariable = variable },
        };

        WebApiServerSettings settings = WebApiServerConfigRules.Create(config, new WebApiServerComponents());

        Assert.Equal("server.pfx", settings.Https.CertificatePath);
        Assert.Equal("pfx-password-value", settings.Https.CertificatePassword);
    }

    [Fact]
    public void Https_RejectsAMissingPasswordVariable_NamingTheVariableButNotItsValue()
    {
        string variable = "JUNEVY_TEST_UNSET_" + Guid.NewGuid().ToString("N");
        var config = new WebApiServerConfig
        {
            Urls = new[] { "https://127.0.0.1:0" },
            Https = new WebApiServerHttpsOptions { CertificatePath = "server.pfx", CertificatePasswordVariable = variable },
        };

        AssertRejected(config, "config.Https.CertificatePasswordVariable", variable);
    }

    [Fact]
    public void Https_IsNotValidatedWithoutAnHttpsAddress()
    {
        var config = new WebApiServerConfig
        {
            Urls = new[] { "http://*:8080" },
            Https = new WebApiServerHttpsOptions { CertificatePasswordVariable = "JUNEVY_TEST_UNSET_" + Guid.NewGuid().ToString("N") },
        };

        WebApiServerSettings settings = WebApiServerConfigRules.Create(config, new WebApiServerComponents());

        Assert.Same(ServerHttpsSettings.NotRequired, settings.Https);
    }

    [Theory]
    [InlineData("MaxConcurrentRequests")]
    [InlineData("RequestTimeout")]
    [InlineData("MaxRequestBodySize")]
    [InlineData("StopTimeout")]
    public void Limits_RejectValuesThatAreNotPositive(string property)
    {
        var config = new WebApiServerConfig();
        SetNonPositive(config, property);

        AssertRejected(config, "config." + property, "greater than zero");
    }

    [Theory]
    [InlineData(199)]
    [InlineData(600)]
    public void BusinessFailureStatusCode_OutsideTheRange_IsRejected(int statusCode)
    {
        var config = new WebApiServerConfig { BusinessFailureStatusCode = statusCode };

        AssertRejected(config, "config.BusinessFailureStatusCode", "200 and 599");
    }

    [Theory]
    [InlineData(200)]
    [InlineData(599)]
    public void BusinessFailureStatusCode_AtTheBoundaries_IsAccepted(int statusCode)
    {
        var config = new WebApiServerConfig { BusinessFailureStatusCode = statusCode };

        WebApiServerSettings settings = WebApiServerConfigRules.Create(config, new WebApiServerComponents());

        Assert.Equal(statusCode, settings.BusinessFailureStatusCode);
    }

    [Fact]
    public void Envelope_WhenDisabled_DoesNotCheckItsFields()
    {
        var config = new WebApiServerConfig { Envelope = new ResponseEnvelopeOptions { Enabled = false, CodePath = "a..b", SuccessCodes = Array.Empty<string>() } };

        WebApiServerSettings settings = WebApiServerConfigRules.Create(config, new WebApiServerComponents());

        Assert.False(settings.Envelope.Enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a..b")]
    [InlineData(".code")]
    [InlineData("code.")]
    [InlineData("result. .code")]
    public void Envelope_WhenEnabled_RejectsPathsWithEmptySegments(string path)
    {
        var config = new WebApiServerConfig { Envelope = new ResponseEnvelopeOptions { Enabled = true, CodePath = path } };

        AssertRejected(config, "config.Envelope.CodePath", "dotted path");
    }

    [Fact]
    public void Envelope_WhenEnabled_RequiresAtLeastOneSuccessCode()
    {
        var config = new WebApiServerConfig { Envelope = new ResponseEnvelopeOptions { Enabled = true, SuccessCodes = Array.Empty<string>() } };

        AssertRejected(config, "config.Envelope.SuccessCodes", "at least one");
    }

    [Fact]
    public void Envelope_WhenEnabled_SplitsThePathsAndKeepsTheFirstSuccessCode()
    {
        var config = new WebApiServerConfig
        {
            Envelope = new ResponseEnvelopeOptions { Enabled = true, CodePath = "result.code", MessagePath = "result.msg", DataPath = "result.data", SuccessCodes = new[] { "00", "0" } },
        };

        WebApiServerSettings settings = WebApiServerConfigRules.Create(config, new WebApiServerComponents());

        Assert.Equal(new[] { "result", "code" }, settings.Envelope.CodePath);
        Assert.Equal(new[] { "result", "msg" }, settings.Envelope.MessagePath);
        Assert.Equal(new[] { "result", "data" }, settings.Envelope.DataPath);
        Assert.Equal("00", settings.Envelope.SuccessCode);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    public void Serializer_WithANonJsonContentType_IsRejected(string contentType)
    {
        var components = new WebApiServerComponents { Serializer = new FixedContentTypeSerializer(contentType) };

        var error = Assert.Throws<ArgumentException>(() => WebApiServerConfigRules.Create(new WebApiServerConfig(), components));
        Assert.Equal("components.Serializer", error.ParamName);
    }

    [Theory]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/problem+json")]
    public void Serializer_WithAJsonContentType_IsAccepted(string contentType)
    {
        var serializer = new FixedContentTypeSerializer(contentType);
        var components = new WebApiServerComponents { Serializer = serializer };

        WebApiServerSettings settings = WebApiServerConfigRules.Create(new WebApiServerConfig(), components);

        Assert.Same(serializer, settings.Serializer);
    }

    [Theory]
    [InlineData("Bad Header")]
    [InlineData("")]
    [InlineData("X:Y")]
    public void Deduplication_RejectsAnInvalidHeaderName(string headerName)
    {
        var config = new WebApiServerConfig { Deduplication = new WebApiServerDeduplicationOptions { HeaderName = headerName } };

        AssertRejected(config, "config.Deduplication.HeaderName", "header name");
    }

    [Fact]
    public void Deduplication_RejectsAWindowAndMaxEntriesThatAreNotPositive()
    {
        var zeroWindow = new WebApiServerConfig { Deduplication = new WebApiServerDeduplicationOptions { Window = 0 } };
        var zeroEntries = new WebApiServerConfig { Deduplication = new WebApiServerDeduplicationOptions { MaxEntries = 0 } };

        AssertRejected(zeroWindow, "config.Deduplication.Window", "greater than zero");
        AssertRejected(zeroEntries, "config.Deduplication.MaxEntries", "greater than zero");
    }

    [Fact]
    public void Logging_RejectsANegativeBodyLength()
    {
        var config = new WebApiServerConfig { Logging = new WebApiLogOptions { MaxLoggedBodyLength = -1 } };

        AssertRejected(config, "config.Logging.MaxLoggedBodyLength", "negative");
    }

    [Fact]
    public void Logging_RejectsNullRedactionLists()
    {
        var config = new WebApiServerConfig { Logging = new WebApiLogOptions { RedactedHeaders = null! } };

        AssertRejected(config, "config.Logging.RedactedHeaders", "null");
    }

    [Fact]
    public void Auth_Basic_ReadsBothVariablesIntoTheCredentials()
    {
        string user = SetVariable("alice");
        string password = SetVariable("s3cret");
        var config = new WebApiServerConfig
        {
            Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Basic, UserNameVariable = user, PasswordVariable = password },
        };

        WebApiServerSettings settings = WebApiServerConfigRules.Create(config, new WebApiServerComponents());

        Assert.Equal(WebApiAuthScheme.Basic, settings.Credentials.Scheme);
        Assert.Equal("alice", settings.Credentials.UserName);
        Assert.Equal("s3cret", settings.Credentials.Password);
    }

    [Fact]
    public void Auth_Basic_WithAMissingPasswordVariable_NamesTheVariableOnly()
    {
        string user = SetVariable("alice");
        string missing = "JUNEVY_TEST_UNSET_" + Guid.NewGuid().ToString("N");
        var config = new WebApiServerConfig
        {
            Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Basic, UserNameVariable = user, PasswordVariable = missing },
        };

        AssertRejected(config, "config.Auth.PasswordVariable", missing);
    }

    [Fact]
    public void Auth_Basic_WithoutAVariableName_IsRejected()
    {
        var config = new WebApiServerConfig { Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Basic } };

        AssertRejected(config, "config.Auth.UserNameVariable", "must name an environment variable");
    }

    [Fact]
    public void Auth_Bearer_ReadsTheTokenVariable()
    {
        string token = SetVariable("tok-123");
        var config = new WebApiServerConfig { Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Bearer, TokenVariable = token } };

        WebApiServerSettings settings = WebApiServerConfigRules.Create(config, new WebApiServerComponents());

        Assert.Equal("tok-123", settings.Credentials.Token);
    }

    [Fact]
    public void Auth_ApiKey_RejectsAnInvalidHeaderName()
    {
        string key = SetVariable("key-value");
        var config = new WebApiServerConfig
        {
            Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.ApiKey, ApiKeyHeader = "Bad Header", ApiKeyVariable = key },
        };

        AssertRejected(config, "config.Auth.ApiKeyHeader", "header name");
    }

    [Fact]
    public void Auth_IsNotReadWhenACustomAuthenticatorIsSet()
    {
        var authenticator = new NoopAuthenticator();
        var config = new WebApiServerConfig
        {
            Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Basic, UserNameVariable = "JUNEVY_TEST_UNSET_" + Guid.NewGuid().ToString("N") },
        };
        var components = new WebApiServerComponents { Authenticator = authenticator };

        WebApiServerSettings settings = WebApiServerConfigRules.Create(config, components);

        Assert.Same(authenticator, settings.CustomAuthenticator);
        Assert.Equal(WebApiAuthScheme.None, settings.Credentials.Scheme);
    }

    [Fact]
    public void Components_InterceptorsMustNotContainNull()
    {
        var components = new WebApiServerComponents();
        components.Interceptors.Add(null!);

        var error = Assert.Throws<ArgumentException>(() => WebApiServerConfigRules.Create(new WebApiServerConfig(), components));
        Assert.Equal("components.Interceptors", error.ParamName);
    }

    [Fact]
    public void Settings_AreASnapshot_UnaffectedByLaterChangesToTheConfiguration()
    {
        var config = new WebApiServerConfig
        {
            Urls = new[] { "http://*:8080" },
            Envelope = new ResponseEnvelopeOptions { Enabled = true },
            Deduplication = new WebApiServerDeduplicationOptions { Enabled = false },
            Logging = new WebApiLogOptions { RedactedHeaders = new[] { "X-Secret" } },
        };
        WebApiServerSettings settings = WebApiServerConfigRules.Create(config, new WebApiServerComponents());

        config.Urls[0] = "http://*:9999";
        config.Envelope.Enabled = false;
        config.Deduplication.Enabled = true;
        config.Logging.RedactedHeaders[0] = "Changed";

        Assert.Equal(new[] { "http://*:8080" }, settings.Urls);
        Assert.True(settings.Envelope.Enabled);
        Assert.False(settings.Deduplication.Enabled);
        Assert.Equal(new[] { "X-Secret" }, settings.Logging.RedactedHeaders);
    }

    [Fact]
    public void Settings_CopyTheInterceptorsAtCreation()
    {
        var components = new WebApiServerComponents();
        components.Interceptors.Add(new NoopInterceptor());

        WebApiServerSettings settings = WebApiServerConfigRules.Create(new WebApiServerConfig(), components);
        components.Interceptors.Add(new NoopInterceptor());

        Assert.Single(settings.Interceptors);
    }

    public void Dispose()
    {
        foreach (string name in variables)
            Environment.SetEnvironmentVariable(name, null);
    }

    private static void SetNonPositive(WebApiServerConfig config, string property)
    {
        switch (property)
        {
            case nameof(WebApiServerConfig.MaxConcurrentRequests):
                config.MaxConcurrentRequests = 0;
                break;
            case nameof(WebApiServerConfig.RequestTimeout):
                config.RequestTimeout = -1;
                break;
            case nameof(WebApiServerConfig.MaxRequestBodySize):
                config.MaxRequestBodySize = 0;
                break;
            case nameof(WebApiServerConfig.StopTimeout):
                config.StopTimeout = 0;
                break;
            default:
                throw new ArgumentException("Unknown limit property.", nameof(property));
        }
    }

    private static void AssertRejected(WebApiServerConfig config, string paramName, string? messageFragment = null)
    {
        var error = Assert.Throws<ArgumentException>(() => WebApiServerConfigRules.Create(config, new WebApiServerComponents()));
        Assert.Equal(paramName, error.ParamName);
        if (messageFragment != null)
            Assert.Contains(messageFragment, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private string SetVariable(string value)
    {
        string name = "JUNEVY_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, value);
        variables.Add(name);
        return name;
    }

    private sealed class FixedContentTypeSerializer : IWebApiSerializer
    {
        public FixedContentTypeSerializer(string contentType)
        {
            ContentType = contentType;
        }

        public string ContentType { get; }

        public byte[] Serialize(object value, Type type) => Array.Empty<byte>();

        public object? Deserialize(byte[] data, Type type) => null;
    }

    private sealed class NoopAuthenticator : IWebApiRequestAuthenticator
    {
        public Task<WebApiAuthenticationResult> AuthenticateAsync(WebApiServerContext context, CancellationToken cancellationToken)
            => Task.FromResult(WebApiAuthenticationResult.Success());
    }

    private sealed class NoopInterceptor : WebApiServerInterceptor
    {
    }
}
