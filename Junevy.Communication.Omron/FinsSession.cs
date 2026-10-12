using Junevy.Communication.Omron.Protocol;

namespace Junevy.Communication.Omron;

/// <summary>
/// 一次会话的不可变快照（Q27、P4-D18）：只保存节点地址握手的最终结果——一条可直接写进请求头的路由，握手前为 null。
/// FINS 的单包上限是固定值（P4-D12），不随会话变化，因此快照里没有其它字段。快照整体替换，读取方看到的永远是完整一致的路由。
/// </summary>
internal sealed class FinsSessionState
{
    /// <summary>握手前的快照：节点号还不可用，因此没有路由。</summary>
    public static readonly FinsSessionState BeforeHandshake = new FinsSessionState(null);

    private FinsSessionState(FinsRoute? route) => Route = route;

    /// <summary>握手得到的路由；尚未握手时为 null。</summary>
    public FinsRoute? Route { get; }

    /// <summary>创建握手后的快照。节点号的合法性由握手与协议层校验，这里不再重复判断。</summary>
    /// <param name="route">握手合成的路由。</param>
    /// <returns>快照。</returns>
    public static FinsSessionState AfterHandshake(FinsRoute route) => new FinsSessionState(route);
}

/// <summary>
/// 会话状态（计划 Task 13 第 2 条）：握手写入、客户端钩子与心跳探测读取。初始化器与客户端共用同一个实例，
/// 每次握手用 <see cref="Volatile"/> 替换整个快照引用（Q27）；重连后 PLC 另派节点号时旧快照整体作废。
/// </summary>
internal sealed class FinsSession
{
    private FinsSessionState state = FinsSessionState.BeforeHandshake;

    /// <summary>当前快照。</summary>
    public FinsSessionState Current => Volatile.Read(ref state);

    /// <summary>握手成功后写入路由。</summary>
    /// <param name="route">握手合成的路由。</param>
    public void ReplaceAfterHandshake(FinsRoute route) => Volatile.Write(ref state, FinsSessionState.AfterHandshake(route));
}
