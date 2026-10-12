namespace Junevy.Communication.Omron.Protocol;

/// <summary>
/// FINS 的 SID（8 位）的进程级计数器（P4-D17）：客户端请求与心跳探测共用，同一通道上不会撞号。取低 8 位，
/// 因此每 256 帧回绕一次；SID 只用来把应答与请求配好顺序（<c>CorrelationMode.Sequential</c>，P4-D8），回绕不改变配对。
/// </summary>
internal static class FinsSessionIds
{
    private static int counter;

    /// <summary>取得下一个 SID（低 8 位）。</summary>
    /// <returns>SID。</returns>
    public static byte Next() => unchecked((byte)Interlocked.Increment(ref counter));
}
