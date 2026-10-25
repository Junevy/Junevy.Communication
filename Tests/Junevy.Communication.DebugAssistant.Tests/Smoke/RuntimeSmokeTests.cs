using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa;
using Junevy.Communication.WebApi.Server;

namespace Junevy.Communication.DebugAssistant.Tests.Smoke;

/// <summary>
/// 开发期验证（DA 计划 Task 0 第 4 条）：应用引用的协议包在 WPF 进程里可以正常加载与运行。
/// </summary>
public sealed class RuntimeSmokeTests
{
    [Fact]
    public async Task WebApiServer_StartsOnAFreePort_AndStops()
    {
        await using var server = new WebApiServer("smoke", new WebApiServerConfig { Urls = ["http://127.0.0.1:0"] });
        server.MapGet<string>("/ping", (_, _) => Task.FromResult(WebApiServerResponse<string>.Ok("pong")));

        CommResult started = await server.StartAsync();

        Assert.True(started.IsSuccess, started.ErrorMessage);
        Assert.Single(server.Urls);
        await server.StopAsync();
        Assert.Equal(WebApiServerState.Stopped, server.State);
    }

    [Fact]
    public async Task OpcUaClient_LoadsAndReportsAnUnreachableServer_WithoutBindingErrors()
    {
        await using var client = OpcUaClient.Create(new OpcUaClientOptions
        {
            EndpointUrl = "opc.tcp://127.0.0.1:1/",
            ApplicationName = "DebugAssistant.Smoke",
            PkiRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "junevy-debug-assistant-smoke-pki"),
            ConnectTimeout = 3000,
        });

        CommResult connected = await client.ConnectAsync();

        Assert.False(connected.IsSuccess);
    }
}
