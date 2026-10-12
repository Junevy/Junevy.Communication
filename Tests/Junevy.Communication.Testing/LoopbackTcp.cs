using Junevy.Communication.Channels;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.Testing;

/// <summary>
/// 回环 TCP 的客户端配置：端到端用例反复拼装"`127.0.0.1` + 端口 + 重连 / 心跳选项"，这里是那一份的单一出处。
/// </summary>
/// <remarks>
/// 每次调用返回**新**对象，调用方可以继续改（例如把端口换成脚本服务器的随机端口）。
/// 用协议的标准端口而不是 0：<c>TcpClientChannel</c> 在构造期校验配置，端口为 0 会先于被测的守卫抛出。
/// </remarks>
public static class LoopbackTcp
{
    /// <summary>回环地址。</summary>
    public const string Host = "127.0.0.1";

    /// <summary>只设地址与端口：不开重连、不开心跳。</summary>
    /// <param name="port">目标端口（通常是脚本服务器的端口，或协议的标准端口）。</param>
    /// <returns>新的客户端配置。</returns>
    public static TcpClientChannelConfig Client(int port)
        => new TcpClientChannelConfig { Host = Host, Port = port };

    /// <summary>加上重连：断开后按固定间隔重连。</summary>
    /// <param name="port">目标端口。</param>
    /// <param name="reconnectIntervalMs">重连间隔（毫秒）。</param>
    /// <returns>新的客户端配置。</returns>
    public static TcpClientChannelConfig Reconnecting(int port, int reconnectIntervalMs = 100)
        => new TcpClientChannelConfig
        {
            Host = Host,
            Port = port,
            Reconnect = new ReconnectOptions { Enabled = true, Interval = reconnectIntervalMs },
        };

    /// <summary>加上心跳与重连：用于"静默设备 → 心跳失效 → 回到 Reconnecting"这类故障用例。</summary>
    /// <param name="port">目标端口。</param>
    /// <param name="intervalMs">心跳间隔（毫秒）。</param>
    /// <param name="timeoutMs">单次心跳的超时（毫秒）。</param>
    /// <param name="maxFailures">连续失败多少次判定链路失效。</param>
    /// <param name="reconnectIntervalMs">重连间隔（毫秒）。</param>
    /// <returns>新的客户端配置。</returns>
    public static TcpClientChannelConfig WithHeartbeat(
        int port,
        int intervalMs = 100,
        int timeoutMs = 200,
        int maxFailures = 2,
        int reconnectIntervalMs = 200)
        => new TcpClientChannelConfig
        {
            Host = Host,
            Port = port,
            Heartbeat = new HeartbeatOptions
            {
                Enabled = true,
                Interval = intervalMs,
                Timeout = timeoutMs,
                MaxFailures = maxFailures,
            },
            Reconnect = new ReconnectOptions { Enabled = true, Interval = reconnectIntervalMs },
        };
}