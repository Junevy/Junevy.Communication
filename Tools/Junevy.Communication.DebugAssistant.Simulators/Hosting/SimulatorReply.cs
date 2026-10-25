namespace Junevy.Communication.DebugAssistant.Simulators.Hosting;

/// <summary>模拟设备的传输方式：TCP（流，多会话）、UDP（数据报）与串口（流，单连接）。</summary>
public enum SimulatorTransport
{
    Tcp,
    Udp,
    Serial,
}

/// <summary>引擎对一个请求的结论：应答、不应答或断开连接。</summary>
public enum SimulatorReplyKind
{
    Reply,
    NoReply,
    Disconnect,
}

/// <summary>
/// 引擎处理一个请求的结果。<see cref="Response"/> 只在 <see cref="SimulatorReplyKind.Reply"/> 时有值；
/// <see cref="Summary"/> 是请求日志中的一行摘要（功能码或命令、地址、数量）。
/// </summary>
public sealed record SimulatorReply(SimulatorReplyKind Kind, byte[]? Response, string Summary)
{
    public static SimulatorReply Respond(byte[] response, string summary) => new(SimulatorReplyKind.Reply, response, summary);

    public static SimulatorReply Silent(string summary) => new(SimulatorReplyKind.NoReply, null, summary);

    public static SimulatorReply Close(string summary) => new(SimulatorReplyKind.Disconnect, null, summary);
}
