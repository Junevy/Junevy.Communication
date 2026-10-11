using Junevy.Communication.WebApi.Auth;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="AuthInterceptorFactory.FromOptions"/> 的测试：从环境变量读取凭据；变量缺失时只报变量名，绝不出现值。
/// 每个测试使用唯一的变量名，结束时清除。
/// </summary>
public sealed class AuthInterceptorFactoryTests : IDisposable
{
    private const string SecretValue = "s3cr3t-value-that-must-never-be-logged";

    private readonly string suffix = Guid.NewGuid().ToString("N");
    private readonly List<string> variables = new List<string>();

    public void Dispose()
    {
        foreach (string name in variables)
            Environment.SetEnvironmentVariable(name, null);
    }

    [Fact]
    public void None_ReturnsNull()
    {
        Assert.Null(AuthInterceptorFactory.FromOptions(new WebApiAuthOptions { Scheme = WebApiAuthScheme.None }));
    }

    [Fact]
    public async Task Basic_ReadsBothVariables()
    {
        string user = SetVariable("USER", "Aladdin");
        string password = SetVariable("PASSWORD", "open sesame");
        var options = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Basic, UserNameVariable = user, PasswordVariable = password };

        IWebApiInterceptor? interceptor = AuthInterceptorFactory.FromOptions(options);

        var context = TestContexts.Sending();
        await interceptor!.OnSendingAsync(context, CancellationToken.None);
        Assert.Equal("Basic QWxhZGRpbjpvcGVuIHNlc2FtZQ==", context.Headers["Authorization"]);
    }

    [Fact]
    public async Task Bearer_ReadsTheTokenVariable()
    {
        string token = SetVariable("TOKEN", "abc.def");
        var options = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Bearer, TokenVariable = token };

        IWebApiInterceptor? interceptor = AuthInterceptorFactory.FromOptions(options);

        var context = TestContexts.Sending();
        await interceptor!.OnSendingAsync(context, CancellationToken.None);
        Assert.Equal("Bearer abc.def", context.Headers["Authorization"]);
    }

    [Fact]
    public async Task ApiKey_ReadsTheKeyVariableAndUsesTheConfiguredHeader()
    {
        string key = SetVariable("KEY", "k-123");
        var options = new WebApiAuthOptions { Scheme = WebApiAuthScheme.ApiKey, ApiKeyHeader = "X-Mes-Key", ApiKeyVariable = key };

        IWebApiInterceptor? interceptor = AuthInterceptorFactory.FromOptions(options);

        var context = TestContexts.Sending();
        await interceptor!.OnSendingAsync(context, CancellationToken.None);
        Assert.Equal("k-123", context.Headers["X-Mes-Key"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrEmptyVariable_ThrowsNamingTheVariableButNotAnyValue(bool defineAsEmpty)
    {
        string user = SetVariable("USER", SecretValue);
        string password = $"JV_TEST_UNSET_{suffix}";
        variables.Add(password);
        if (defineAsEmpty)
            Environment.SetEnvironmentVariable(password, "");
        var options = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Basic, UserNameVariable = user, PasswordVariable = password };

        var exception = Assert.Throws<ArgumentException>(() => AuthInterceptorFactory.FromOptions(options));

        Assert.Contains(password, exception.Message);
        Assert.DoesNotContain(SecretValue, exception.Message);
        Assert.Equal("Auth.PasswordVariable", exception.ParamName);
    }

    [Fact]
    public void MissingVariable_ForEachScheme_NamesTheVariable()
    {
        string missing = $"JV_TEST_MISSING_{suffix}";
        WebApiAuthOptions[] cases =
        {
            new WebApiAuthOptions { Scheme = WebApiAuthScheme.Bearer, TokenVariable = missing },
            new WebApiAuthOptions { Scheme = WebApiAuthScheme.ApiKey, ApiKeyVariable = missing },
        };

        foreach (WebApiAuthOptions options in cases)
        {
            var exception = Assert.Throws<ArgumentException>(() => AuthInterceptorFactory.FromOptions(options));
            Assert.Contains(missing, exception.Message);
        }
    }

    private string SetVariable(string role, string value)
    {
        string name = $"JV_TEST_{role}_{suffix}";
        variables.Add(name);
        Environment.SetEnvironmentVariable(name, value);
        return name;
    }
}
