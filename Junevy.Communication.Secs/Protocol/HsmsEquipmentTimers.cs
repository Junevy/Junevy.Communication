namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// 设备端要用的计时器与上限（内部快照，P51-D5、P51-D9）。公开的
/// <c>HsmsEquipmentOptions</c> 在 <c>Junevy.Communication.Secs.Hsms</c>（Task 8），
/// 它在 <c>HsmsEquipment.Create</c> 时校验并取快照，之后改选项对象不影响已建好的设备。
/// </summary>
internal sealed class HsmsEquipmentTimers
{
    /// <summary>取计时器快照。</summary>
    /// <param name="t3">T3，应答超时（毫秒）。</param>
    /// <param name="t5">T5，连接间隔（毫秒）。</param>
    /// <param name="t6">T6，控制事务超时（毫秒）。</param>
    /// <param name="t7">T7，未选中超时（毫秒）。</param>
    /// <param name="t8">T8，字符间隔超时（毫秒）。</param>
    /// <param name="linktestInterval">Linktest 心跳间隔（毫秒），0 表示不发。</param>
    /// <param name="maxMessageLength">单条消息的最大长度（字节），同时作为分帧的 MaxFrameLength。</param>
    internal HsmsEquipmentTimers(int t3, int t5, int t6, int t7, int t8, int linktestInterval, int maxMessageLength)
    {
        T3 = t3;
        T5 = t5;
        T6 = t6;
        T7 = t7;
        T8 = t8;
        LinktestInterval = linktestInterval;
        MaxMessageLength = maxMessageLength;
    }

    /// <summary>T3，应答超时（毫秒）——每个带 W 位的主消息的 <c>RequestOptions.Timeout</c>。</summary>
    internal int T3 { get; }

    /// <summary>T5，连接间隔（毫秒）——Active 的 <c>ReconnectOptions</c>。</summary>
    internal int T5 { get; }

    /// <summary>T6，控制事务超时（毫秒）——Select 与 Linktest 请求的超时。</summary>
    internal int T6 { get; }

    /// <summary>T7，未选中超时（毫秒）——Passive 的 <c>SessionHandshakeTimeout</c>。</summary>
    internal int T7 { get; }

    /// <summary>T8，字符间隔超时（毫秒）——<c>PartialFrameTimeout</c>。</summary>
    internal int T8 { get; }

    /// <summary>Linktest 心跳间隔（毫秒），0 表示不发 Linktest。</summary>
    internal int LinktestInterval { get; }

    /// <summary>单条消息的最大长度（字节），同时作为分帧的 <c>MaxFrameLength</c>（P51-D5）。</summary>
    internal int MaxMessageLength { get; }
}