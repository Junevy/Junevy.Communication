namespace Junevy.Communication.Core.Utils;

/// <summary>
/// 进程内递增的序号发生器，给协议报文中"用来把应答与请求配好"的字段取值（请求号、会话号、事务号一类）：
/// 线程安全，按宽度取低位回绕（16 位每 65536 个、8 位每 256 个回到 0），第一次取值是 1。
/// </summary>
/// <remarks>
/// 一个实例只按一种宽度使用——两个方法共用同一个计数，混用时两种宽度的序号都会跳号。
/// 要让多个通道不撞号，就让它们共用一个进程级实例。回绕是安全的，前提是同一个号在它的应答返回之前不会再被取到：
/// 每个通道同一时刻只有少量请求在途，16 位或 8 位的范围足够让旧号早已应答完毕。
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