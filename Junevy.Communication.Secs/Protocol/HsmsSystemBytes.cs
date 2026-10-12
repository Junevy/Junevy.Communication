using Junevy.Communication.Core.Utils;

namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// System Bytes 的发生器（P51-D8）：进程级唯一，取 <see cref="SequenceCounter"/> 的低 16 位零扩展成 4 字节。
/// </summary>
/// <remarks>
/// System Bytes 只要求在**发送方**内唯一，设备端与主机各用各的计数器，因此不需要协商。
/// 本库只用 16 位（第 19 节的"未经真机验证"清单第 ② 条）；16 位在途唯一对
/// "同一连接上同时只有少量未应答事务"的 SECS-II 已经足够。
/// </remarks>
internal static class HsmsSystemBytes
{
    private static readonly SequenceCounter Counter = new SequenceCounter();

    /// <summary>取下一个 System Bytes。</summary>
    /// <returns>低 16 位零扩展的 4 字节序号。</returns>
    internal static uint Next() => Counter.NextUInt16();
}