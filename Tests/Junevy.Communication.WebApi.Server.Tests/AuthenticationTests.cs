using Junevy.Communication.WebApi.Server.Auth;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Pipeline;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 内置认证（P32-D9）：Basic、Bearer、ApiKey 的成功与失败，方案名不区分大小写，失败的原因不含期望的凭据，挑战头与自定义认证器的优先级。
/// </summary>
public sealed class AuthenticationTests
{
    private const string Password = "s3cret-value";

    private static readonly ServerCredentials BasicCredentials = new ServerCredentials(WebApiAuthScheme.Basic, "alice", Password, null, "X-API-Key", null);
    private static readonly ServerCredentials BearerCredentials = new ServerCredentials(WebApiAuthScheme.Bearer, null, null, "token-123", "X-API-Key", null);
    private static readonly ServerCredentials ApiKeyCredentials = new ServerCredentials(WebApiAuthScheme.ApiKey, null, null, null, "X-Device-Key", "key-abc");

    [Fact]
    public async Task Basic_AcceptsTheConfiguredPair_AndExposesTheUserName()
    {
        var authenticator = new CredentialAuthenticator(BasicCredentials);

        WebApiAuthenticationResult result = await authenticator.AuthenticateAsync(
            RequestFactory.Create(new Dictionary<string, string> { ["Authorization"] = RequestFactory.Basic("alice:" + Password) }),
            CancellationToken.None);

        Assert.True(result.IsAuthenticated);
        Assert.Equal("alice", result.UserName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer token-123")]
    [InlineData("Basic !!!not-base64")]
    [InlineData("Basic YWxpY2U=")]
    [InlineData("Basic YWxpY2U6d3Jvbmc=")]
    [InlineData("Basic Ym9iOnMzY3JldC12YWx1ZQ==")]
    public async Task Basic_RejectsMissingWrongSchemeMalformedAndWrongCredentials(string authorization)
    {
        var headers = authorization.Length == 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Authorization"] = authorization };
        var authenticator = new CredentialAuthenticator(BasicCredentials);

        WebApiAuthenticationResult result = await authenticator.AuthenticateAsync(RequestFactory.Create(headers), CancellationToken.None);

        Assert.False(result.IsAuthenticated);
        Assert.NotNull(result.FailureMessage);
    }

    [Fact]
    public async Task Basic_FailureReason_NeverContainsTheExpectedPassword()
    {
        var authenticator = new CredentialAuthenticator(BasicCredentials);

        WebApiAuthenticationResult result = await authenticator.AuthenticateAsync(
            RequestFactory.Create(new Dictionary<string, string> { ["Authorization"] = RequestFactory.Basic("alice:wrong") }),
            CancellationToken.None);

        Assert.DoesNotContain(Password, result.FailureMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Basic_SchemeNameIsCaseInsensitive()
    {
        var authenticator = new CredentialAuthenticator(BasicCredentials);
        string header = "basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("alice:" + Password));

        WebApiAuthenticationResult result = await authenticator.AuthenticateAsync(
            RequestFactory.Create(new Dictionary<string, string> { ["Authorization"] = header }),
            CancellationToken.None);

        Assert.True(result.IsAuthenticated);
    }

    [Fact]
    public async Task Bearer_AcceptsTheConfiguredToken_AndRejectsAnyOther()
    {
        var authenticator = new CredentialAuthenticator(BearerCredentials);

        WebApiAuthenticationResult accepted = await authenticator.AuthenticateAsync(
            RequestFactory.Create(new Dictionary<string, string> { ["Authorization"] = "Bearer token-123" }),
            CancellationToken.None);
        WebApiAuthenticationResult rejected = await authenticator.AuthenticateAsync(
            RequestFactory.Create(new Dictionary<string, string> { ["Authorization"] = "Bearer token-999" }),
            CancellationToken.None);
        WebApiAuthenticationResult missing = await authenticator.AuthenticateAsync(RequestFactory.Create(), CancellationToken.None);

        Assert.True(accepted.IsAuthenticated);
        Assert.False(rejected.IsAuthenticated);
        Assert.False(missing.IsAuthenticated);
        Assert.DoesNotContain("token-123", rejected.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiKey_ReadsTheConfiguredHeader()
    {
        var authenticator = new CredentialAuthenticator(ApiKeyCredentials);

        WebApiAuthenticationResult accepted = await authenticator.AuthenticateAsync(
            RequestFactory.Create(new Dictionary<string, string> { ["x-device-key"] = "key-abc" }),
            CancellationToken.None);
        WebApiAuthenticationResult wrongValue = await authenticator.AuthenticateAsync(
            RequestFactory.Create(new Dictionary<string, string> { ["X-Device-Key"] = "key-xyz" }),
            CancellationToken.None);
        WebApiAuthenticationResult wrongHeader = await authenticator.AuthenticateAsync(
            RequestFactory.Create(new Dictionary<string, string> { ["X-API-Key"] = "key-abc" }),
            CancellationToken.None);

        Assert.True(accepted.IsAuthenticated);
        Assert.False(wrongValue.IsAuthenticated);
        Assert.False(wrongHeader.IsAuthenticated);
    }

    [Fact]
    public void ServerAuthenticator_WithoutAScheme_IsNull()
    {
        WebApiServerSettings settings = SettingsFactory.Create();

        Assert.Null(ServerAuthenticator.Create(settings, "mes"));
    }

    [Fact]
    public void ServerAuthenticator_Basic_ChallengesWithTheServerRealm()
    {
        WebApiServerSettings settings = SettingsFactory.Create(config =>
        {
            config.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Basic, UserNameVariable = "PATH", PasswordVariable = "PATH" };
        });

        ServerAuthenticator? authenticator = ServerAuthenticator.Create(settings, "mes");

        Assert.NotNull(authenticator);
        Assert.Equal("Basic realm=\"mes\"", authenticator!.Challenge);
    }

    [Fact]
    public void ServerAuthenticator_Bearer_ChallengesWithBearer_AndApiKeyHasNoChallenge()
    {
        WebApiServerSettings bearer = SettingsFactory.Create(config =>
        {
            config.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Bearer, TokenVariable = "PATH" };
        });
        WebApiServerSettings apiKey = SettingsFactory.Create(config =>
        {
            config.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.ApiKey, ApiKeyVariable = "PATH" };
        });

        Assert.Equal("Bearer", ServerAuthenticator.Create(bearer, "mes")!.Challenge);
        Assert.Null(ServerAuthenticator.Create(apiKey, "mes")!.Challenge);
    }

    [Fact]
    public void ServerAuthenticator_CustomAuthenticator_WinsOverTheConfiguredScheme()
    {
        var custom = new AlwaysAuthenticated();
        WebApiServerSettings settings = SettingsFactory.Create(
            config => config.Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Basic, UserNameVariable = "PATH", PasswordVariable = "PATH" },
            new WebApiServerComponents { Authenticator = custom });

        ServerAuthenticator? authenticator = ServerAuthenticator.Create(settings, "mes");

        Assert.Same(custom, authenticator!.Authenticator);
        Assert.Null(authenticator.Challenge);
    }

    [Fact]
    public async Task EveryScheme_DecidesThroughTheComparer_WithThePresentedValueFirst()
    {
        // 计时无法在用例里区分；改为确认每一次比较都经过比较函数、结论也由它决定（计划第 17 节）。
        var calls = new List<(string Presented, string Expected)>();
        bool Refuse(string presented, string expected)
        {
            calls.Add((presented, expected));
            return false;
        }

        WebApiAuthenticationResult basic = await new CredentialAuthenticator(BasicCredentials, Refuse).AuthenticateAsync(
            RequestFactory.Create(new Dictionary<string, string> { ["Authorization"] = RequestFactory.Basic("alice:" + Password) }), CancellationToken.None);
        WebApiAuthenticationResult bearer = await new CredentialAuthenticator(BearerCredentials, Refuse).AuthenticateAsync(
            RequestFactory.Create(new Dictionary<string, string> { ["Authorization"] = "Bearer token-123" }), CancellationToken.None);
        WebApiAuthenticationResult apiKey = await new CredentialAuthenticator(ApiKeyCredentials, Refuse).AuthenticateAsync(
            RequestFactory.Create(new Dictionary<string, string> { ["X-Device-Key"] = "key-abc" }), CancellationToken.None);

        Assert.False(basic.IsAuthenticated);
        Assert.False(bearer.IsAuthenticated);
        Assert.False(apiKey.IsAuthenticated);
        Assert.Equal(new[] { ("alice", "alice"), (Password, Password), ("token-123", "token-123"), ("key-abc", "key-abc") }, calls);
    }

    [Theory]
    [InlineData("token-123", "token-123", true)]
    [InlineData("token-124", "token-123", false)]
    [InlineData("token-12", "token-123", false)]
    [InlineData("", "token-123", false)]
    [InlineData("密钥", "密钥", true)]
    public void ConstantTimeText_ComparesTheValues(string presented, string expected, bool same)
    {
        Assert.Equal(same, ConstantTimeText.Matches(presented, expected));
    }

    private sealed class AlwaysAuthenticated : IWebApiRequestAuthenticator
    {
        public Task<WebApiAuthenticationResult> AuthenticateAsync(WebApiServerContext context, CancellationToken cancellationToken)
            => Task.FromResult(WebApiAuthenticationResult.Success("custom"));
    }
}
