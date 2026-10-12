using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 端到端用例的会话（计划 Task 14 第 2 条）：在回环 TCP 上承载 <see cref="FinsDeviceSimulator"/>，创建客户端并完成节点地址握手，
/// 释放时先释放客户端再释放设备宿主。与只答固定向量的 <see cref="FinsCannedPlc"/> 相对，本会话背后是有内存的设备替身。
/// </summary>
internal sealed class FinsConnectedPlc : IAsyncDisposable
{
    private readonly DeviceSimulatorTcpHost host;

    private FinsConnectedPlc(FinsDeviceSimulator device, DeviceSimulatorTcpHost host, FinsClient client)
    {
        Device = device;
        this.host = host;
        Client = client;
    }

    /// <summary>设备替身：用例用它预置或核对内存、切换故障模式与统计握手次数。</summary>
    public FinsDeviceSimulator Device { get; }

    /// <summary>已握手的客户端。</summary>
    public FinsClient Client { get; }

    /// <summary>建立会话：宿主 → 创建客户端 → 连接（连接即握手，P4-D18）。</summary>
    /// <param name="options">客户端选项；为 null 时使用默认值，即客户端节点号自动分配。</param>
    /// <param name="configureTransport">额外的通道配置（心跳、重连等）。</param>
    /// <returns>已连接的会话。</returns>
    public static async Task<FinsConnectedPlc> StartAsync(
        FinsClientOptions? options = null, Action<TcpClientChannelConfig>? configureTransport = null)
    {
        var device = new FinsDeviceSimulator();
        DeviceSimulatorTcpHost host = device.HostTcp();
        var transport = new TcpClientChannelConfig { Host = "127.0.0.1", Port = host.Port };
        configureTransport?.Invoke(transport);

        var client = FinsClient.Create(transport, options ?? new FinsClientOptions());
        Succeeded(await client.ConnectAsync());
        return new FinsConnectedPlc(device, host, client);
    }

    /// <summary>释放客户端与设备宿主。</summary>
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await host.DisposeAsync();
    }
}
