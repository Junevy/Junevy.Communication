using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Siemens.Protocol;

namespace Junevy.Communication.Siemens;

/// <summary>
/// S7 客户端的协议钩子（计划第 13 节第 1 条）：字节钩子直接调用协议层的读写编码器；
/// 字钩子把偏移与数量乘以 2 后交给同一个字节编码器，不重复编码代码（S7 按字节寻址，宽度字母不改变编号，P4-D16）。
/// 写位每包一位，绝对位号 = 字节号 × 8 + 位号（没有位号视为 0）+ 包内偏移（P4-D12）；
/// S7 没有位软元件，因此不覆写读位钩子，位读取由套件改为按字节读（P4-D6）。
/// 单包上限取自会话快照，范围校验在发出任何请求之前完成。
/// </summary>
public sealed partial class S7Client
{
    /// <summary>P4-D12：写位一包只有一位。</summary>
    private const int BitPointsPerPacket = 1;

    private const int BytesPerWord = 2;

    // 地址字段是 24 位的位地址（17.3 节），因此字节号要乘以 8 才等于该字段。
    private const int BitsPerByte = 8;

    // 构造请求时记下的引用号，解析同一包的应答时使用；操作锁保证一包的发与收不被其它包打断。
    private ushort lastPduReference;

    /// <inheritdoc />
    protected override bool TryParseAddress(string address, [NotNullWhen(true)] out S7Address? parsed, [NotNullWhen(false)] out string? error)
        => S7AddressParser.TryParse(address, out parsed, out error);

    /// <inheritdoc />
    protected override int GetMaxReadPoints(S7Address address, PlcAccess access)
    {
        S7SessionState snapshot = RequireSession();

        // S7 没有位软元件：位读取由套件改成按字节读（P4-D6），因此除字访问外都按字节计。
        return access == PlcAccess.Words ? snapshot.MaxReadWords : snapshot.MaxReadBytes;
    }

    /// <inheritdoc />
    protected override int GetMaxWritePoints(S7Address address, PlcAccess access)
    {
        S7SessionState snapshot = RequireSession();
        return access switch
        {
            PlcAccess.Words => snapshot.MaxWriteWords,
            PlcAccess.Bits => BitPointsPerPacket,
            _ => snapshot.MaxWriteBytes,
        };
    }

    /// <inheritdoc />
    protected override void BuildReadBytesRequest(S7Address address, int offset, int count, IBufferWriter<byte> output)
        => output.Write(S7ReadVar.BuildRequest(NextReference(), address.Device.AreaCode, address.DbNumber, address.Number + offset, count));

    /// <inheritdoc />
    protected override CommResult<byte[]> ParseReadBytesResponse(int count, ReadOnlySpan<byte> frame)
        => S7ReadVar.ReadResponse(frame, lastPduReference, count);

    /// <inheritdoc />
    protected override void BuildWriteBytesRequest(S7Address address, int offset, ReadOnlySpan<byte> bytes, IBufferWriter<byte> output)
        => output.Write(S7WriteVar.BuildRequest(NextReference(), address.Device.AreaCode, address.DbNumber, address.Number + offset, bytes));

    /// <inheritdoc />
    protected override CommResult ParseWriteResponse(ReadOnlySpan<byte> frame)
        => S7WriteVar.ReadResponse(frame, lastPduReference);

    // —— 字钩子：偏移与数量都乘以 2 后走字节钩子（计划第 13 节第 1 条）——

    /// <inheritdoc />
    protected override void BuildReadWordsRequest(S7Address address, int offset, int count, IBufferWriter<byte> output)
        => BuildReadBytesRequest(address, BytesPerWord * offset, BytesPerWord * count, output);

    /// <inheritdoc />
    protected override CommResult<byte[]> ParseReadWordsResponse(int count, ReadOnlySpan<byte> frame)
        => ParseReadBytesResponse(BytesPerWord * count, frame);

    /// <inheritdoc />
    protected override void BuildWriteWordsRequest(S7Address address, int offset, ReadOnlySpan<byte> words, IBufferWriter<byte> output)
        => BuildWriteBytesRequest(address, BytesPerWord * offset, words, output);

    /// <inheritdoc />
    protected override bool CanWriteBitsNatively(S7Address address) => true;

    /// <inheritdoc />
    protected override void BuildWriteBitsRequest(S7Address address, int offset, ReadOnlySpan<bool> values, IBufferWriter<byte> output)
    {
        // 绝对位号拆回字节号与位号：跨字节时后面的点自然落到下一个字节（17.1 节说明段）。
        int absoluteBit = BitsPerByte * address.Number + (address.BitIndex ?? 0) + offset;
        output.Write(S7WriteVar.BuildBitRequest(NextReference(), address.Device.AreaCode, address.DbNumber,
                                                absoluteBit / BitsPerByte, absoluteBit % BitsPerByte, values[0]));
    }

    /// <summary>
    /// 检查整个操作的末端字节：字访问每点占 2 字节，字节访问每点 1 字节，位访问按绝对位号换算（与 <see cref="BuildWriteBitsRequest"/> 同一算法）。
    /// 末端超过软元件的最大字节号（24 位地址字段所能表示的字节号）时返回 InvalidRequest。
    /// </summary>
    /// <inheritdoc />
    protected override CommResult ValidateRange(S7Address address, PlcAccess access, int count)
    {
        // 用 long 计算：count 接近 int 上限时会溢出。
        long last = access switch
        {
            PlcAccess.Words => (long)address.Number + (long)BytesPerWord * count - 1,
            PlcAccess.Bits => ((long)BitsPerByte * address.Number + (address.BitIndex ?? 0) + count - 1) / BitsPerByte,
            _ => (long)address.Number + count - 1,
        };

        int maxByteNumber = address.Device.MaxNumber;
        return last > maxByteNumber
            ? CommResult.Fail($"The access on '{address.Text}' ends at byte {last}, beyond the address field limit {maxByteNumber}.",
                              CommErrorKind.InvalidRequest)
            : CommResult.Success();
    }

    // 钩子只在握手之后被调用，此时会话一定有协商结果；没有值说明接线错误（计划第 13 节第 1 条），异常由流水线收口为 Unspecified。
    private S7SessionState RequireSession()
    {
        S7SessionState snapshot = session.Current;
        if (snapshot.NegotiatedPduBytes is null)
            throw new InvalidOperationException(
                "The S7 packet limits are unavailable because the Setup Communication handshake has not run on this channel.");

        return snapshot;
    }

    private ushort NextReference() => lastPduReference = S7Message.PduReferences.NextUInt16();
}
