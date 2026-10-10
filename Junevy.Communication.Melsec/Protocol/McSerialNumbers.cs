namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// 4E 序列号的进程级计数器（P2-D11）：客户端请求与心跳探测共用，同一通道上不会撞号。取低 16 位。
/// </summary>
internal static class McSerialNumbers
{
    private static int counter;

    /// <summary>取得下一个序列号（低 16 位）。</summary>
    /// <returns>序列号。</returns>
    public static ushort Next() => unchecked((ushort)Interlocked.Increment(ref counter));
}
