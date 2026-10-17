using System.Net.Http;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Rest;
using Junevy.Communication.WebApi.Server.Formatting;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 两端约定一致的证据（P32 Task 2 第 2 条）：服务端写出的包络交给 3.1 客户端解析，客户端拿到同样的数据；业务失败拿到 <c>RemoteError</c> 与业务码。
/// 不使用套接字：经一个返回固定字节的 <see cref="HttpMessageHandler"/> 传递。
/// </summary>
public sealed class ResponseRoundTripTests
{
    [Fact]
    public async Task ServerEnvelope_IsReadBackByTheClient_WithTheSameData()
    {
        ServerResponse wire = new ResponseFormatter(SettingsFactory.Create(config => config.Envelope = SettingsFactory.NestedEnvelope()))
            .Format(WebApiServerResponse<Order>.Ok(new Order { Id = 42, Name = "widget" }));

        WebApiResult<Order> result = await SendThroughClientAsync(wire);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Data!.Id);
        Assert.Equal("widget", result.Data.Name);
    }

    [Fact]
    public async Task ServerBusinessFailure_IsReadBackByTheClient_AsRemoteErrorWithTheBusinessCode()
    {
        ServerResponse wire = new ResponseFormatter(SettingsFactory.Create(config => config.Envelope = SettingsFactory.NestedEnvelope()))
            .Format(WebApiServerResponse<Order>.Fail("E42", "rejected"));

        WebApiResult<Order> result = await SendThroughClientAsync(wire);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal("E42", result.RemoteCode);
    }

    [Fact]
    public async Task ServerNullData_IsReadBackByTheClient_AsSuccessWithoutData()
    {
        ServerResponse wire = new ResponseFormatter(SettingsFactory.Create(config => config.Envelope = SettingsFactory.NestedEnvelope()))
            .Format(WebApiServerResponse<Order>.Ok(null));

        WebApiResult<Order> result = await SendThroughClientAsync(wire);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Data);
    }

    private static async Task<WebApiResult<Order>> SendThroughClientAsync(ServerResponse wire)
    {
        using var handler = new FixedResponseHandler(wire);
        var config = new WebApiClientConfig
        {
            BaseUrl = "http://mes.test/",
            Envelope = SettingsFactory.NestedEnvelope(),
        };
        using var client = new WebApiClient("mes", config, null, new WebApiComponents { HttpHandler = handler });

        return await client.SendAsync<Order>(new WebApiRequest { Path = "orders/42" });
    }
}
