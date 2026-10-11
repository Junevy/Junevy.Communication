using System.Text;
using Junevy.Communication.WebApi.Auth;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="WebApiAuthentication"/> 的内置认证拦截器测试（P3-D4）：写入的头、每次尝试重新取值、敏感头标记与参数校验。
/// </summary>
public sealed class WebApiAuthenticationTests
{
    [Fact]
    public async Task Basic_Rfc7617Example_ProducesTheDocumentedHeader()
    {
        var context = TestContexts.Sending();

        await WebApiAuthentication.Basic("Aladdin", "open sesame").OnSendingAsync(context, CancellationToken.None);

        Assert.Equal("Basic QWxhZGRpbjpvcGVuIHNlc2FtZQ==", context.Headers["Authorization"]);
    }

    [Fact]
    public async Task Basic_NonAsciiCredentials_AreEncodedAsUtf8()
    {
        var context = TestContexts.Sending();

        await WebApiAuthentication.Basic("测试用户", "密码").OnSendingAsync(context, CancellationToken.None);

        string expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("测试用户:密码"));
        Assert.Equal("Basic " + expected, context.Headers["Authorization"]);
    }

    [Fact]
    public void Basic_UserNameWithColon_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => WebApiAuthentication.Basic("a:b", "secret"));

        Assert.Equal("userName", exception.ParamName);
    }

    [Fact]
    public async Task Bearer_Token_WritesTheAuthorizationHeader()
    {
        var context = TestContexts.Sending();

        await WebApiAuthentication.Bearer("abc.def").OnSendingAsync(context, CancellationToken.None);

        Assert.Equal("Bearer abc.def", context.Headers["Authorization"]);
    }

    [Fact]
    public async Task Bearer_Provider_IsCalledOncePerAttempt()
    {
        int calls = 0;
        IWebApiInterceptor interceptor = WebApiAuthentication.Bearer(_ => Task.FromResult("token-" + Interlocked.Increment(ref calls)));
        var first = TestContexts.Sending(attempt: 1);
        var second = TestContexts.Sending(attempt: 2);

        await interceptor.OnSendingAsync(first, CancellationToken.None);
        await interceptor.OnSendingAsync(second, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal("Bearer token-1", first.Headers["Authorization"]);
        Assert.Equal("Bearer token-2", second.Headers["Authorization"]);
    }

    [Fact]
    public async Task Bearer_Provider_ReceivesTheAttemptToken()
    {
        using var source = new CancellationTokenSource();
        CancellationToken seen = default;
        IWebApiInterceptor interceptor = WebApiAuthentication.Bearer(token =>
        {
            seen = token;
            return Task.FromResult("t");
        });

        await interceptor.OnSendingAsync(TestContexts.Sending(), source.Token);

        Assert.Equal(source.Token, seen);
    }

    [Fact]
    public async Task Bearer_ProviderReturningEmpty_Throws()
    {
        IWebApiInterceptor interceptor = WebApiAuthentication.Bearer(_ => Task.FromResult(""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => interceptor.OnSendingAsync(TestContexts.Sending(), CancellationToken.None));
    }

    [Fact]
    public async Task ApiKey_WritesTheNamedHeader()
    {
        var context = TestContexts.Sending();

        await WebApiAuthentication.ApiKey("X-Api-Token", "k-123").OnSendingAsync(context, CancellationToken.None);

        Assert.Equal("k-123", context.Headers["x-api-token"]);
        Assert.False(context.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Interceptor_ReplacesAnExistingHeaderWithTheSameName()
    {
        var context = TestContexts.Sending(headers: new Dictionary<string, string> { ["authorization"] = "stale" });

        await WebApiAuthentication.Bearer("fresh").OnSendingAsync(context, CancellationToken.None);

        Assert.Single(context.Headers);
        Assert.Equal("Bearer fresh", context.Headers["Authorization"]);
    }

    [Fact]
    public async Task EveryBuiltInInterceptor_MarksItsHeaderSensitive()
    {
        IWebApiInterceptor[] interceptors =
        {
            WebApiAuthentication.Basic("u", "p"),
            WebApiAuthentication.Bearer("t"),
            WebApiAuthentication.Bearer(_ => Task.FromResult("t")),
        };
        foreach (IWebApiInterceptor interceptor in interceptors)
        {
            var context = TestContexts.Sending();
            await interceptor.OnSendingAsync(context, CancellationToken.None);
            Assert.Contains("Authorization", context.SensitiveHeaders, StringComparer.OrdinalIgnoreCase);
        }

        var apiKeyContext = TestContexts.Sending();
        await WebApiAuthentication.ApiKey("X-Api-Token", "k").OnSendingAsync(apiKeyContext, CancellationToken.None);
        Assert.Contains("X-Api-Token", apiKeyContext.SensitiveHeaders, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnReceived_DoesNothing()
    {
        var context = TestContexts.Received(401);

        await WebApiAuthentication.Bearer("t").OnReceivedAsync(context, CancellationToken.None);

        Assert.False(context.ReplayRequested);
        Assert.Null(context.Failure);
    }

    [Fact]
    public void NullOrEmptyArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => WebApiAuthentication.Basic(null!, "p"));
        Assert.Throws<ArgumentException>(() => WebApiAuthentication.Basic("", "p"));
        Assert.Throws<ArgumentNullException>(() => WebApiAuthentication.Basic("u", null!));
        Assert.Throws<ArgumentException>(() => WebApiAuthentication.Basic("u", ""));
        Assert.Throws<ArgumentNullException>(() => WebApiAuthentication.Bearer((string)null!));
        Assert.Throws<ArgumentException>(() => WebApiAuthentication.Bearer(""));
        Assert.Throws<ArgumentNullException>(() => WebApiAuthentication.Bearer((Func<CancellationToken, Task<string>>)null!));
        Assert.Throws<ArgumentNullException>(() => WebApiAuthentication.ApiKey(null!, "k"));
        Assert.Throws<ArgumentException>(() => WebApiAuthentication.ApiKey("", "k"));
        Assert.Throws<ArgumentNullException>(() => WebApiAuthentication.ApiKey("X-Key", null!));
        Assert.Throws<ArgumentException>(() => WebApiAuthentication.ApiKey("X-Key", ""));
    }
}
