namespace Junevy.Communication.Core.Utils;

/// <summary>
/// 进程内递增的序号发生器，给协议报文中"用来把应答与请求配好"的字段取值（请求号、会话号、事务号一类）：
/// 线程安全，按宽度取低位回绕（16 位每 65536 个、8 位每 256 个回到 0），第一次取值是 1。
/// </summary>
/// <remarks>
/// 一个实例只按一种宽度使用——两个方法读的是同一个计数器，只是截断的位数不同，混用会让 8 位的序号在每 256 次后重复一次。
/// 要让多个通道不撞号，就让它们共用一个进程级实例（配合 <c>CorrelationMode.Sequential</c> 回绕不影响配对）。
/// </remarks>
public sealed class SequenceCounter
{
    // 用 int 累加并按宽度截断：int 溢出后 unchecked 的截断结果与从 0 重新计数一致，因此不需要额外的进位处理。
    private int counter;

    /// <summary>取下一个序号的低 16 位。</summary>
    /// <returns>序号；第一次调用返回 1，65535 之后回到 0。</returns>
    public ushort NextUInt16() => unchecked((ushort)Interlocked.Increment(ref counter));

    /// <summary>取下一个序号的低 8 位。</summary>
    /// <returns>序号；第一次调用返回 1，255 之后回到 0。</returns>
    public byte NextByte() => unchecked((byte)Interlocked.Increment(ref counter));
}