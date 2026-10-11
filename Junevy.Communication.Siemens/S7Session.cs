namespace Junevy.Communication.Siemens;

/// <summary>
/// 一次会话的不可变快照（Q27、P4-D9）：目前只保存握手协商的 PDU 长度，握手前为 null。
/// 快照整体替换，读取方看到的永远是完整一致的会话，而不是半更新的字段。
/// </summary>
internal sealed class S7SessionState
{
    /// <summary>握手前的快照：没有协商结果。</summary>
    public static readonly S7SessionState BeforeHandshake = new S7SessionState(null);

    private S7SessionState(int? negotiatedPduBytes)
        => NegotiatedPduBytes = negotiatedPduBytes;

    /// <summary>握手协商出的 PDU 字节数；尚未握手时为 null。</summary>
    public int? NegotiatedPduBytes { get; }

    /// <summary>创建握手后的快照。长度是否可用由协议层校验（P4-D13），这里不再重复判断。</summary>
    /// <param name="negotiatedPduBytes">协商的 PDU 字节数。</param>
    /// <returns>快照。</returns>
    public static S7SessionState AfterHandshake(int negotiatedPduBytes) => new S7SessionState(negotiatedPduBytes);
}

/// <summary>
/// 会话状态（计划第 12 节第 2 条）：握手写入、客户端读取。初始化器与客户端共用同一个实例，
/// 每次握手用 <see cref="Volatile"/> 替换整个快照引用（Q27）。
/// </summary>
internal sealed class S7Session
{
    private S7SessionState state = S7SessionState.BeforeHandshake;

    /// <summary>当前快照。</summary>
    public S7SessionState Current => Volatile.Read(ref state);

    /// <summary>握手成功后写入协商结果。</summary>
    /// <param name="negotiatedPduBytes">协商的 PDU 字节数。</param>
    public void ReplaceAfterHandshake(int negotiatedPduBytes)
        => Volatile.Write(ref state, S7SessionState.AfterHandshake(negotiatedPduBytes));
}
