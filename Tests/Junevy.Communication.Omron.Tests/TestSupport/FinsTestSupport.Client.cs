using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// <see cref="FinsTestSupport"/> 的 Task 13 部分：客户端、握手与探测用例要读的帧内字段位置、
/// 两条派生向量，以及"按实际 SID 与负载重新封装应答"的构造助手。
/// </summary>
internal static partial class FinsTestSupport
{
    // 魔数与长度字段合计 8 字节，长度字段的值不含它们（第 17.2 节说明段）。
    private const int MagicAndLengthBytes = 8;

    /// <summary>读参数的字地址高字节位置（区域代码之后 2 字节）。</summary>
    public const int WordAddressHighIndex = ShellBytes + 13;

    /// <summary>读参数的位号位置（字地址之后 1 字节）。</summary>
    public const int BitNumberIndex = ShellBytes + 15;

    /// <summary>读参数的数量高字节位置（位号之后 2 字节）。</summary>
    public const int QuantityHighIndex = ShellBytes + 16;

    /// <summary>P4-D18：请求头里 DA1（服务端节点号）的位置。</summary>
    public const int DestinationNodeIndex = ShellBytes + 4;

    /// <summary>P4-D18：请求头里 SA1（客户端节点号）的位置。</summary>
    public const int SourceNodeIndex = ShellBytes + 7;

    /// <summary>结束码之后的数据起点：外壳 16 + 头 10 + 命令回显 2 + 结束码 2。</summary>
    public const int ResponsePayloadIndex = ShellBytes + 14;

    /// <summary>请求的数据起点：内存区参数 6 字节（区域 1 + 字号 2 + 位号 1 + 数量 2）写在回显之后。</summary>
    public const int RequestPayloadIndex = ShellBytes + 18;

    // ———— 由 17.2 节向量派生的两条（改动点写在注释里）————

    /// <summary>P4-D15：心跳探测的请求——读 D0 的 1 字（由 V-F-3 改字号 0 与数量 1，SID 仍为 0x01）。</summary>
    public const string Probe_ReadD0 = "46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 01 01 01 82 00 00 00 00 01";

    /// <summary>由 V-F-2 派生：应答把客户端节点改为 0x0C（重连后 PLC 另派节点号的场景）。</summary>
    public const string NodeResponse_ClientNode0C = "46 49 4E 53 00 00 00 10 00 00 00 01 00 00 00 00 00 00 00 0C 00 00 00 01";

    // ———— 帧内字段读取与应答构造 ————

    /// <summary>请求帧的 SID（进程级计数器给的号，向量里写死的那字节比较前先替换）。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>SID。</returns>
    public static byte SessionIdOf(byte[] frame) => frame[SidIndex];

    /// <summary>内存区读写的服务码回显位置（0101 读、0102 写）。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>服务码。</returns>
    public static ushort ServiceOf(byte[] frame) => (ushort)(frame[CommandIndex] << 8 | frame[CommandIndex + 1]);

    /// <summary>内存区读写的区域代码（第 17.3 节）。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>区域代码。</returns>
    public static byte AreaCodeOf(byte[] frame) => frame[AreaCodeIndex];

    /// <summary>把向量的 SID 改成实际请求使用的那个。</summary>
    /// <param name="vector">原始向量。</param>
    /// <param name="sessionId">实际 SID。</param>
    /// <returns>改写后的帧字节。</returns>
    public static byte[] WithSessionId(string vector, byte sessionId) => Mutated(vector, SidIndex, sessionId);

    /// <summary>读参数的字地址。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>字号。</returns>
    public static int WordAddressOf(byte[] frame) => frame[WordAddressHighIndex] << 8 | frame[WordAddressHighIndex + 1];

    /// <summary>读参数的位号。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>位号。</returns>
    public static int BitNumberOf(byte[] frame) => frame[BitNumberIndex];

    /// <summary>读参数的数量（字访问按字计、位访问按点计）。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>数量。</returns>
    public static int QuantityOf(byte[] frame) => frame[QuantityHighIndex] << 8 | frame[QuantityHighIndex + 1];

    /// <summary>替身收到的最后一条该服务的请求帧（读位与写位之后还有心跳，因此不能直接取末条）。</summary>
    /// <param name="device">设备替身。</param>
    /// <param name="service">服务码（0101 读、0102 写）。</param>
    /// <returns>请求帧。</returns>
    public static byte[] LastRequest(FinsDeviceSimulator device, ushort service)
        => device.Requests.Last(frame => ServiceOf(frame) == service);

    /// <summary>按实际 SID 与负载构造读应答（模板 V-F-4）：外壳长度与 SID 同步改写。</summary>
    /// <param name="sessionId">请求的 SID。</param>
    /// <param name="payload">结束码之后的数据字节。</param>
    /// <returns>完整的应答帧。</returns>
    public static byte[] ReadAck(byte sessionId, byte[] payload)
    {
        var ack = new byte[ResponsePayloadIndex + payload.Length];
        ToBytes(V_F_4).AsSpan(0, ResponsePayloadIndex).CopyTo(ack);
        payload.CopyTo(ack, ResponsePayloadIndex);
        return Sealed(ack, sessionId);
    }

    /// <summary>按实际 SID 构造写应答（模板 V-F-5 的应答，不含数据）。</summary>
    /// <param name="sessionId">请求的 SID。</param>
    /// <returns>完整的应答帧。</returns>
    public static byte[] WriteAck(byte sessionId) => Sealed(ToBytes(V_F_5_Response), sessionId);

    /// <summary>构造节点地址应答（模板 V-F-2）：改写体里的两个节点号。</summary>
    /// <param name="clientNode">客户端节点号。</param>
    /// <param name="serverNode">服务端节点号。</param>
    /// <returns>完整的应答帧。</returns>
    public static byte[] NodeAck(byte clientNode, byte serverNode)
    {
        byte[] frame = ToBytes(V_F_2);
        frame[ClientNodeTailIndex] = clientNode;
        frame[ServerNodeTailIndex] = serverNode;
        return frame;
    }

    // 应答的 SID 与外壳长度字段（长度 = 帧长 − 8，见 FinsTcpFrame 的说明）随负载改写。
    private static byte[] Sealed(byte[] frame, byte sessionId)
    {
        frame[SidIndex] = sessionId;
        uint declared = (uint)(frame.Length - MagicAndLengthBytes);
        frame[TcpLengthIndex] = (byte)(declared >> 24);
        frame[TcpLengthIndex + 1] = (byte)(declared >> 16);
        frame[TcpLengthIndex + 2] = (byte)(declared >> 8);
        frame[TcpLengthIndex + 3] = (byte)declared;
        return frame;
    }
}
