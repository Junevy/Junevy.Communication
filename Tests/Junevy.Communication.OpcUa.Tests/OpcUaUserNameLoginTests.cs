using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 用户名密码登录（P62-D3 第 ③ 步留的位置）：对的凭据能连上并能读，错的凭据很快得到
/// <see cref="CommErrorKind.AuthenticationFailed"/> 而不是阻塞到超时，服务器没有用户名策略时 <c>NotSupported</c>，
/// 匿名的行为不变，重连沿用同一身份。需要真实套接字，放进 <c>SocketTiming</c> 集合。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class OpcUaUserNameLoginTests
{
    private const string UserName = "operator";
    private const string Password = "s3cret-密码";
    private const int ConnectTimeoutMilliseconds = 10000;

    private readonly ILoggerFactory loggerFactory;

    public OpcUaUserNameLoginTests()
        => loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));

    private static OpcUaClientOptions OptionsFor(string endpointUrl, string? userName = null, string? password = null)
        => new OpcUaClientOptions
        {
            EndpointUrl = endpointUrl,
            PkiRoot = OpcUaTestServerFactory.UniquePkiRoot(),
            UserName = userName,
            Password = password,
            ConnectTimeout = ConnectTimeoutMilliseconds,
            OperationTimeout = 5000,
            SessionTimeout = 30000,
            KeepAliveInterval = 1000,
            ReconnectInterval = 200,
            MaxReconnectInterval = 1000,
        };

    private Task<OpcUaTestServer> StartUserNameServerAsync(bool allowAnonymous = false, string password = Password)
        => OpcUaTestServerFactory.StartOwnAsync(
            options =>
            {
                options.AllowAnonymous = allowAnonymous;
                options.UserName = UserName;
                options.Password = password;
            },
            loggerFactory);

    [Fact]
    public async Task Connect_CorrectCredentials_ConnectsAndReads()
    {
        await using OpcUaTestServer server = await StartUserNameServerAsync();
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl, UserName, Password), loggerFactory);

        CommResult result = await client.ConnectAsync();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(OpcUaSessionState.Connected, client.State);
        Assert.Equal(1, server.SessionCount);
        Assert.True((await client.ReadAsync(new[] { server.Id("Test/Int32").ToString()! })).IsSuccess);
    }

    [Theory]
    [InlineData(UserName, "wrong")]
    [InlineData("intruder", Password)]
    public async Task Connect_WrongCredentials_ReturnsAuthenticationFailedWithoutWaitingForTheTimeout(string userName, string password)
    {
        await using OpcUaTestServer server = await StartUserNameServerAsync();
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl, userName, password), loggerFactory);

        DateTime started = DateTime.UtcNow;
        CommResult result = await client.ConnectAsync();
        TimeSpan elapsed = DateTime.UtcNow - started;

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.InRange(elapsed.TotalMilliseconds, 0, ConnectTimeoutMilliseconds * 0.8);
        Assert.DoesNotContain(password, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(OpcUaSessionState.Disconnected, client.State);
        Assert.Equal(0, server.SessionCount);
    }

    [Fact]
    public async Task Connect_NullPassword_SendsAnEmptyPassword()
    {
        await using OpcUaTestServer server = await StartUserNameServerAsync(password: string.Empty);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl, UserName, password: null), loggerFactory);

        CommResult result = await client.ConnectAsync();

        Assert.True(result.IsSuccess, result.ToString());
    }

    [Fact]
    public async Task Connect_ServerWithoutUserNamePolicy_ReturnsNotSupported()
    {
        // 默认服务器只公布匿名策略。
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl, UserName, Password), loggerFactory);

        CommResult result = await client.ConnectAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.NotSupported, result.ErrorKind);
        Assert.Contains("user name login", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, server.SessionCount);
    }

    [Fact]
    public async Task Connect_AnonymousClientOnAServerThatOffersBoth_StillConnectsAnonymously()
    {
        await using OpcUaTestServer server = await StartUserNameServerAsync(allowAnonymous: true);
        await using OpcUaClient anonymous = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);
        await using OpcUaClient named = OpcUaClient.Create(OptionsFor(server.EndpointUrl, UserName, Password), loggerFactory);

        CommResult anonymousResult = await anonymous.ConnectAsync();
        CommResult namedResult = await named.ConnectAsync();

        Assert.True(anonymousResult.IsSuccess, anonymousResult.ToString());
        Assert.True(namedResult.IsSuccess, namedResult.ToString());
        Assert.Equal(2, server.SessionCount);
    }

    [Fact]
    public async Task Connect_AnonymousClientOnAUserNameOnlyServer_ReturnsNotSupported()
    {
        await using OpcUaTestServer server = await StartUserNameServerAsync();
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        CommResult result = await client.ConnectAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.NotSupported, result.ErrorKind);
        Assert.Contains("anonymous login", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerRestarts_TheClientReconnectsWithTheSameCredentials()
    {
        await using OpcUaTestServer server = await StartUserNameServerAsync();
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl, UserName, Password), loggerFactory);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        await server.StopHostAsync();
        await WaitForStateAsync(client, OpcUaSessionState.Reconnecting);

        await server.StartHostAsync();

        await WaitForStateAsync(client, OpcUaSessionState.Connected);
        Assert.True((await client.ReadAsync(new[] { server.Id("Test/Int32").ToString()! })).IsSuccess);
    }

    [Fact]
    public async Task Create_TakesASnapshotOfTheCredentials()
    {
        await using OpcUaTestServer server = await StartUserNameServerAsync();
        OpcUaClientOptions options = OptionsFor(server.EndpointUrl, UserName, Password);
        await using OpcUaClient client = OpcUaClient.Create(options, loggerFactory);

        options.Password = "changed";

        Assert.True((await client.ConnectAsync()).IsSuccess);
    }

    private static async Task WaitForStateAsync(OpcUaClient client, OpcUaSessionState wanted)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(30000);
        while (DateTime.UtcNow < deadline)
        {
            if (client.State == wanted)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"The client did not reach {wanted}; it is {client.State}.");
    }
}
