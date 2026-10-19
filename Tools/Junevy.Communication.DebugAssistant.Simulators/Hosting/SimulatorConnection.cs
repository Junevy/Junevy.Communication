namespace Junevy.Communication.DebugAssistant.Simulators.Hosting;

/// <summary>
/// 一个连接（TCP 会话、UDP 对端，或串口）的状态（计划第 8.1 节）。<see cref="Items"/> 保存引擎的会话状态，
/// 例如 S7 的 COTP 与 Setup 进度、FINS 的节点分配；TCP 会话把它指向 <c>ITcpSession.Items</c>。
/// </summary>
public sealed class SimulatorConnection(long id, string? remoteEndPoint, IDictionary<string, object?> items)
{
    /// <summary>连接编号。</summary>
    public long Id { get; } = id;

    /// <summary>对端地址，串口为 null。</summary>
    public string? RemoteEndPoint { get; } = remoteEndPoint;

    /// <summary>引擎的会话状态。</summary>
    public IDictionary<string, object?> Items { get; } = items;
}
