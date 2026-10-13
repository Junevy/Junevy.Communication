using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 测试用的计时器快照（T3 / T5 / T6 / T7 / T8 都调到几百毫秒，计划第 1 节第 7 条），
/// 以及 HSMS 状态变化记录、报文的解析与黄金向量取用。
/// </summary>
internal static class HsmsTimers
{
    /// <summary>分帧与正文解码共用的上限；语义与生产默认（16 MiB）相同，只是更小。</summary>
    internal const int MaxMessageLength = 64 * 1024;

    private const int T3 = 500;
    private const int T8 = 500;

    /// <summary>取一份缩短的计时器快照。</summary>
    /// <param name="t5">T5，连接间隔（毫秒）。</param>
    /// <param name="t6">T6，控制事务超时（毫秒）。</param>
    /// <param name="t7">T7，未选中超时（毫秒）。</param>
    /// <param name="linktestInterval">Linktest 心跳间隔（毫秒）；0 表示不发。</param>
    /// <returns>计时器快照。</returns>
    internal static HsmsEquipmentTimers Fast(int? t5 = null, int? t6 = null, int? t7 = null, int linktestInterval = 0)
        => new HsmsEquipmentTimers(
            t3: T3,
            t5: t5 ?? 200,
            t6: t6 ?? 400,
            t7: t7 ?? 2000,
            t8: T8,
            linktestInterval: linktestInterval,
            maxMessageLength: MaxMessageLength);
}

/// <summary>按发生顺序记录 HSMS 状态变化（Q35）。</summary>
internal sealed class HsmsStateLog
{
    private readonly object sync = new object();
    private readonly List<HsmsStateChange> changes = new List<HsmsStateChange>();

    /// <summary>订阅 <see cref="HsmsSessionState.StateChanged"/>。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="args">事件参数。</param>
    internal void Record(object? sender, HsmsStateChangedEventArgs args)
    {
        lock (sync)
            changes.Add(new HsmsStateChange(args.Previous, args.Current, args.Reason));
    }

    /// <summary>变化记录（快照，按发生顺序）。</summary>
    internal IReadOnlyList<HsmsStateChange> Changes
    {
        get
        {
            lock (sync)
                return changes.ToList();
        }
    }

    /// <summary>状态轨迹，写进断言信息里。</summary>
    internal string Trace
    {
        get
        {
            IReadOnlyList<HsmsStateChange> snapshot = Changes;
            return snapshot.Count == 0
                ? "(no change)"
                : string.Join(" -> ", snapshot.Select(change => change.Current.ToString()));
        }
    }
}

/// <summary>一次状态变化的记录。</summary>
internal sealed class HsmsStateChange
{
    /// <summary>记录一次变化。</summary>
    /// <param name="previous">变化前的状态。</param>
    /// <param name="current">变化后的状态。</param>
    /// <param name="reason">原因说明。</param>
    internal HsmsStateChange(HsmsState previous, HsmsState current, string? reason)
    {
        Previous = previous;
        Current = current;
        Reason = reason;
    }

    /// <summary>变化前的状态。</summary>
    internal HsmsState Previous { get; }

    /// <summary>变化后的状态。</summary>
    internal HsmsState Current { get; }

    /// <summary>原因说明。</summary>
    internal string? Reason { get; }
}

/// <summary>空闲端口：服务端配置要求端口在 1..65535，因此借一个——临时套接字绑定端口 0，读出端口号后立即释放。</summary>
internal static class HsmsPorts
{
    /// <summary>取一个当前空闲的回环端口。</summary>
    /// <returns>端口号。</returns>
    internal static int Free()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}

/// <summary>状态序列的断言：P51-D10 的规则只有一条，因此断言也只有一份（两种模式共用）。</summary>
internal static class HsmsStateAsserts
{
    /// <summary>断言状态依次走过 NotConnected → NotSelected → Selected，各一条，且都带原因说明。</summary>
    /// <param name="states">状态变化记录。</param>
    internal static void SelectSequence(HsmsStateLog states)
    {
        IReadOnlyList<HsmsStateChange> changes = states.Changes;
        Assert.True(changes.Count == 2, $"Expected NotConnected -> NotSelected -> Selected, but got {states.Trace}.");
        Assert.Equal(HsmsState.NotConnected, changes[0].Previous);
        Assert.Equal(HsmsState.NotSelected, changes[0].Current);
        Assert.Equal(HsmsState.Selected, changes[1].Current);
        Assert.False(string.IsNullOrEmpty(changes[1].Reason));
    }
}

/// <summary>报文的解析与黄金向量取用。</summary>
internal static class HsmsTestFrames
{
    /// <summary>System Bytes 在完整报文里的偏移：长度前缀 4 + 会话号 2 + 头字节 2 + 头字节 3 + PType 1 + SType 1。</summary>
    private const int SystemBytesOffset = HsmsMessage.LengthPrefixBytes + 6;

    /// <summary>解析一条主机收到或发出的完整报文；结构不合法时抛出并带上十六进制原文。</summary>
    /// <param name="frame">完整报文（带 4 字节长度前缀）。</param>
    /// <returns>解析结果。</returns>
    internal static HsmsMessage Parse(byte[] frame)
    {
        var parsed = HsmsMessage.Read(frame, HsmsTimers.MaxMessageLength);
        return parsed.IsSuccess
            ? parsed.Data
            : throw new InvalidOperationException($"The frame cannot be parsed ({parsed.ErrorMessage}): {HexVector.ToText(frame)}");
    }

    /// <summary>把数据项编码成字节（给"收到的正文等于期望项"这类断言用）。</summary>
    /// <param name="item">数据项。</param>
    /// <returns>编码结果。</returns>
    internal static byte[] Encode(SecsItem item)
    {
        using var buffer = new ByteBufferWriter();
        SecsItemCodec.Encode(buffer, item);
        return buffer.ToArray();
    }

    /// <summary>取一条报文头部的 10 个字节——S9 消息的正文就是被涉及消息的这 10 个字节（SEMI E5）。</summary>
    /// <param name="frame">完整报文（带 4 字节长度前缀）。</param>
    /// <returns>头部的 10 个字节。</returns>
    internal static byte[] HeaderBytes(byte[] frame)
    {
        var header = new byte[HsmsMessage.HeaderBytes];
        Array.Copy(frame, HsmsMessage.LengthPrefixBytes, header, 0, HsmsMessage.HeaderBytes);
        return header;
    }

    /// <summary>
    /// 把向量的 System Bytes 换成 <paramref name="systemBytes"/>：设备端发出的请求的号由进程级计数器产生，
    /// 因此比对前替换（第 17 节的说明）。
    /// </summary>
    /// <param name="vector">黄金向量。</param>
    /// <param name="systemBytes">实际使用的 System Bytes。</param>
    /// <returns>替换后的十六进制文本。</returns>
    internal static string WithSystemBytes(string vector, uint systemBytes)
    {
        byte[] frame = HexVector.ToBytes(vector);

        // System Bytes 在报文头里的固定位置（长度前缀 4 + 会话号 2 + 头字节 2 + 头字节 3 + PType + SType），
        // 因此控制消息（它是末尾 4 个字节）与带正文的数据消息都替换得对。
        int start = SystemBytesOffset;
        frame[start] = (byte)(systemBytes >> 24);
        frame[start + 1] = (byte)(systemBytes >> 16);
        frame[start + 2] = (byte)(systemBytes >> 8);
        frame[start + 3] = (byte)systemBytes;
        return HexVector.ToText(frame);
    }
}
