using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Plc;

namespace Junevy.Communication.Omron;

/// <summary>
/// FINS 客户端的协议钩子（计划 Task 13 第 3 条）：FINS 的存储区一律按字寻址（P4-D16），字访问用字区代码、位写用位区代码，
/// 两者共用 <c>FinsMemoryArea</c> 的参数编码，只有区域代码与数量单位不同。
/// 写位的绝对位号 = 字号 × 16 + 位号 + 包内偏移，跨字时后面的点自然落到下一字的位号 0（P4-D6、17.2 节 V-F-7）；
/// FINS 没有位软元件，因此不覆写读位钩子，位读取由套件改为读覆盖的字（P4-D6）。字节访问同理由套件换算成整字，不需要字节钩子。
/// 单包上限是固定值（P4-D12：读 999、写 996），不随会话变化；范围校验在发出任何请求之前完成。
/// </summary>
public sealed partial class FinsClient
{
    // P4-D12：内存区读写的数量字段上限；欧姆龙的手册按"读 999 字/点、写 996 字/点"给出，与 CPU 型号无关。
    private const int MaxReadPoints = 999;
    private const int MaxWritePoints = 996;

    // 一个字 16 位（17.2 节 V-F-7 的位号 0–15）；字地址字段是 2 字节，因此可用的最后一个字号是 0xFFFF。
    private const int BitsPerWord = 16;
    private const int MaxWordNumber = 0xFFFF;

    // 构造请求时记下的 SID，解析同一包的应答时使用；操作锁保证一包的发与收不被其它包打断。
    private byte lastSessionId;

    /// <inheritdoc />
    protected override bool TryParseAddress(string address, [NotNullWhen(true)] out FinsAddress? parsed, [NotNullWhen(false)] out string? error)
        => FinsDeviceTable.AddressParser.TryParse(address, out parsed, out error);

    /// <inheritdoc />
    protected override int GetMaxReadPoints(FinsAddress address, PlcAccess access) => MaxReadPoints;

    /// <inheritdoc />
    protected override int GetMaxWritePoints(FinsAddress address, PlcAccess access) => MaxWritePoints;

    /// <inheritdoc />
    protected override void BuildReadWordsRequest(FinsAddress address, int offset, int count, IBufferWriter<byte> output)
        => output.Write(FinsMemoryArea.BuildReadRequest(RequireRoute(), NextSessionId(), address.Device.WordAreaCode,
                                                        address.Number + offset, bitNumber: 0, count));

    /// <inheritdoc />
    protected override CommResult<byte[]> ParseReadWordsResponse(int count, ReadOnlySpan<byte> frame)
        => FinsMemoryArea.ReadResponse(frame, lastSessionId, count * FinsMemoryArea.WordBytes, Logger);

    /// <inheritdoc />
    protected override void BuildWriteWordsRequest(FinsAddress address, int offset, ReadOnlySpan<byte> words, IBufferWriter<byte> output)
        => output.Write(FinsMemoryArea.BuildWriteRequest(RequireRoute(), NextSessionId(), address.Device.WordAreaCode,
                                                         address.Number + offset, words));

    /// <inheritdoc />
    protected override CommResult ParseWriteResponse(ReadOnlySpan<byte> frame)
        => FinsMemoryArea.CheckWriteResponse(frame, lastSessionId, Logger);

    /// <inheritdoc />
    protected override bool CanWriteBitsNatively(FinsAddress address) => true;

    /// <inheritdoc />
    protected override void BuildWriteBitsRequest(FinsAddress address, int offset, ReadOnlySpan<bool> values, IBufferWriter<byte> output)
    {
        // 绝对位号拆回字号与位号：跨字时后面的点自然落到下一字（17.2 节说明段）。
        int absoluteBit = BitsPerWord * address.Number + (address.BitIndex ?? 0) + offset;
        output.Write(FinsMemoryArea.BuildWriteBitsRequest(RequireRoute(), NextSessionId(), address.Device.BitAreaCode,
                                                          absoluteBit / BitsPerWord, absoluteBit % BitsPerWord, values));
    }

    /// <summary>
    /// 检查整个操作的末端字号：字访问的末字是"起始字号 + 字数 − 1"，位访问把"字号 × 16 + 位号 + 点数"换算成覆盖的最后一个字号。
    /// 越过字地址字段所能表示的 0xFFFF 时返回 InvalidRequest（P4-D16）。
    /// </summary>
    /// <inheritdoc />
    protected override CommResult ValidateRange(FinsAddress address, PlcAccess access, int count)
    {
        // 用 long 计算：count 接近 int 上限时 number + count 会溢出。
        long lastWord = access == PlcAccess.Words
            ? (long)address.Number + count - 1
            : ((long)BitsPerWord * address.Number + (address.BitIndex ?? 0) + count - 1) / BitsPerWord;

        return lastWord > MaxWordNumber
            ? CommResult.Fail($"The access on '{address.Text}' ends at word {lastWord}, beyond the address field limit {MaxWordNumber}.",
                              CommErrorKind.InvalidRequest)
            : CommResult.Success();
    }

    // 钩子只在握手之后被调用，此时会话一定有路由；没有路由说明接线错误（计划 Task 13 第 3 条），异常由流水线收口为 Unspecified。
    private FinsRoute RequireRoute()
    {
        FinsRoute? route = session.Current.Route;
        return route ?? throw new InvalidOperationException(
            "The FINS route is unavailable because the node address handshake has not run on this channel.");
    }

    private byte NextSessionId() => lastSessionId = FinsSessionIds.Next();
}
