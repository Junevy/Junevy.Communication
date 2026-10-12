using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Junevy.Communication.Omron.Protocol;

/// <summary>
/// FINS 头的六个路由字节（P4-D18）：目的端是 DNA/DA1/DA2，源端是 SNA/SA1/SA2。
/// DA1 取握手得到的服务端节点号，SA1 取客户端节点号，网络号与单元号来自选项，SNA 与 SA2 固定为 0。
/// </summary>
internal readonly struct FinsRoute
{
    /// <summary>构造一条路由。</summary>
    /// <param name="destinationNetwork">DNA。</param>
    /// <param name="destinationNode">DA1。</param>
    /// <param name="destinationUnit">DA2。</param>
    /// <param name="sourceNetwork">SNA。</param>
    /// <param name="sourceNode">SA1。</param>
    /// <param name="sourceUnit">SA2。</param>
    public FinsRoute(byte destinationNetwork, byte destinationNode, byte destinationUnit, byte sourceNetwork, byte sourceNode, byte sourceUnit)
    {
        DestinationNetwork = destinationNetwork;
        DestinationNode = destinationNode;
        DestinationUnit = destinationUnit;
        SourceNetwork = sourceNetwork;
        SourceNode = sourceNode;
        SourceUnit = sourceUnit;
    }

    /// <summary>DNA。</summary>
    public byte DestinationNetwork { get; }

    /// <summary>DA1：目的节点号。</summary>
    public byte DestinationNode { get; }

    /// <summary>DA2：目的单元号。</summary>
    public byte DestinationUnit { get; }

    /// <summary>SNA。</summary>
    public byte SourceNetwork { get; }

    /// <summary>SA1：源节点号。</summary>
    public byte SourceNode { get; }

    /// <summary>SA2：源单元号。</summary>
    public byte SourceUnit { get; }
}

/// <summary>
/// FINS 头（计划 Task 12 第 3 条与第 17.2 节说明段）：请求写 ICF 0x80、RSV 0、GCT 0x02、六个路由字节与 SID；
/// 应答只校验 ICF 的应答位（0x40）与 SID 回显。RSV、GCT 与应答的路由字节不参与判断（Task 12 第 5 条只列了这两项）。
/// </summary>
internal static class FinsHead
{
    /// <summary>头的字节数：ICF、RSV、GCT、DNA、DA1、DA2、SNA、SA1、SA2、SID。</summary>
    public const int HeadBytes = 10;

    /// <summary>SID 在头里的偏移。</summary>
    private const int SessionIdOffset = HeadBytes - 1;

    // 请求的 ICF：命令帧、主框架（第 17.2 节说明段）。
    private const byte RequestIcf = 0x80;

    // 应答的 ICF 位：向量的应答是 0xC0，置位即可（Task 12 第 5 条）。
    private const byte ResponseIcfBit = 0x40;

    private const byte Reserved = 0x00;
    private const byte GroupCommandTimer = 0x02;

    /// <summary>写入请求头（V-F-3 的 16–25 字节）。</summary>
    /// <param name="buffer">目标缓冲。</param>
    /// <param name="route">路由字节。</param>
    /// <param name="sessionId">SID，同时用作应答的回显校验（P4-D8、P4-D17）。</param>
    public static void WriteRequest(IBufferWriter<byte> buffer, in FinsRoute route, byte sessionId)
    {
        FinsFields.WriteUInt8(buffer, RequestIcf);
        FinsFields.WriteUInt8(buffer, Reserved);
        FinsFields.WriteUInt8(buffer, GroupCommandTimer);
        FinsFields.WriteUInt8(buffer, route.DestinationNetwork);
        FinsFields.WriteUInt8(buffer, route.DestinationNode);
        FinsFields.WriteUInt8(buffer, route.DestinationUnit);
        FinsFields.WriteUInt8(buffer, route.SourceNetwork);
        FinsFields.WriteUInt8(buffer, route.SourceNode);
        FinsFields.WriteUInt8(buffer, route.SourceUnit);
        FinsFields.WriteUInt8(buffer, sessionId);
    }

    /// <summary>校验应答头并前移过它的 10 个字节。</summary>
    /// <param name="content">外壳之后的字节；成功时前移过 FINS 头。</param>
    /// <param name="expectedSessionId">请求使用的 SID。</param>
    /// <param name="violation">结构错误描述；成功时为 null。</param>
    /// <returns>应答头结构正确返回 true。</returns>
    public static bool TryTakeResponse(ref ReadOnlySpan<byte> content, byte expectedSessionId, [NotNullWhen(false)] out string? violation)
    {
        if (content.Length < HeadBytes)
        {
            violation = $"The FINS response is {content.Length} byte(s); the head alone needs {HeadBytes}.";
            return false;
        }

        if ((content[0] & ResponseIcfBit) == 0)
        {
            violation = $"The FINS response ICF 0x{content[0]:X2} does not have the response flag 0x40.";
            return false;
        }

        byte sessionId = content[SessionIdOffset];
        content = content.Slice(HeadBytes);
        if (sessionId != expectedSessionId)
        {
            violation = $"The FINS response SID 0x{sessionId:X2} does not match the request 0x{expectedSessionId:X2}.";
            return false;
        }

        violation = null;
        return true;
    }
}
