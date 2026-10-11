namespace Junevy.Communication.Siemens.Protocol;

/// <summary>
/// S7 的 PDU 引用号（16 位）的进程级计数器（P4-D17）：客户端请求与心跳探测共用，同一通道上不会撞号。取低 16 位。
/// </summary>
internal static class S7PduReferences
{
    private static int counter;

    /// <summary>取得下一个 PDU 引用号（低 16 位）。</summary>
    /// <returns>PDU 引用号。</returns>
    public static ushort Next() => unchecked((ushort)Interlocked.Increment(ref counter));
}
