using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// 端到端用例的会话（计划 Task 10 第 2 条）：在回环 TCP 上承载 <see cref="S7DeviceSimulator"/>，创建客户端并完成握手，
/// 释放时先释放客户端再释放设备宿主。与只答固定向量的 <see cref="S7CannedCpu"/> 相对，本会话背后是有内存的设备替身。
/// </summary>
internal sealed class S7ConnectedCpu : IAsyncDisposable
{
    private readonly DeviceSimulatorTcpHost host;

    private S7ConnectedCpu(S7DeviceSimulator device, DeviceSimulatorTcpHost host, S7Client client)
    {
        Device = device;
        this.host = host;
        Client = client;
    }

    /// <summary>设备替身：用例用它预置或核对内存、切换故障模式与统计握手。</summary>
    public S7DeviceSimulator Device { get; }

    /// <summary>已握手的客户端。</summary>
    public S7Client Client { get; }

    /// <summary>建立会话：宿主 → 创建客户端 → 连接。</summary>
    /// <param name="pdu">替身协商的 PDU 字节数（P4-D13 允许 64 以上，用例取 240 或 960）。</param>
    /// <param name="configureTransport">额外的通道配置（心跳、重连等）。</param>
    /// <param name="options">客户端选项；为 null 时使用默认值。</param>
    /// <returns>已连接的会话。</returns>
    public static async Task<S7ConnectedCpu> StartAsync(
        int pdu = 240, Action<TcpClientChannelConfig>? configureTransport = null, S7ClientOptions? options = null)
    {
        var device = new S7DeviceSimulator(pdu);
        DeviceSimulatorTcpHost host = device.HostTcp();
        var transport = new TcpClientChannelConfig { Host = "127.0.0.1", Port = host.Port };
        configureTransport?.Invoke(transport);

        var client = S7Client.Create(transport, options ?? new S7ClientOptions());
        S7TestSupport.Succeeded(await client.ConnectAsync());
        return new S7ConnectedCpu(device, host, client);
    }

    /// <summary>释放客户端与设备宿主。</summary>
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await host.DisposeAsync();
    }
}
