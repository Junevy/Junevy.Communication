using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Melsec.Protocol;
using Junevy.Communication.Plc;

namespace Junevy.Communication.Melsec;

/// <summary>
/// MELSEC 客户端的协议钩子（P2 计划 Task 11）：每个钩子只做选项与地址的转换，编码与解码交给 <see cref="McFrameExchange"/>。
/// Plc 的钩子是 protected internal，本程序集不是它的友元，因此这里用 protected override。
/// </summary>
public sealed partial class McClient
{
    /// <inheritdoc />
    protected override bool TryParseAddress(string address, [NotNullWhen(true)] out McAddress? parsed, [NotNullWhen(false)] out string? error)
        => McDeviceTable.AddressParser.TryParse(address, out parsed, out error);

    /// <inheritdoc />
    protected override int GetMaxReadPoints(McAddress address, PlcAccess access) => MaxPoints(access);

    /// <inheritdoc />
    protected override int GetMaxWritePoints(McAddress address, PlcAccess access) => MaxPoints(access);

    /// <inheritdoc />
    protected override void BuildReadWordsRequest(McAddress address, int offset, int count, IBufferWriter<byte> output)
        => McFrameOutput.Write(exchange.BatchRead(address.Device, WordHead(address, offset), count, bitUnit: false), output);

    /// <inheritdoc />
    protected override CommResult<byte[]> ParseReadWordsResponse(int count, ReadOnlySpan<byte> frame)
        => exchange.ReadWords(frame, count);

    /// <inheritdoc />
    protected override void BuildWriteWordsRequest(McAddress address, int offset, ReadOnlySpan<byte> words, IBufferWriter<byte> output)
        => McFrameOutput.Write(exchange.BatchWriteWords(address.Device, WordHead(address, offset), words), output);

    /// <inheritdoc />
    protected override void BuildReadBitsRequest(McAddress address, int offset, int count, IBufferWriter<byte> output)
        => McFrameOutput.Write(exchange.BatchRead(address.Device, address.Number + offset, count, bitUnit: true), output);

    /// <inheritdoc />
    protected override CommResult<bool[]> ParseReadBitsResponse(int count, ReadOnlySpan<byte> frame)
        => exchange.ReadBits(frame, count);

    /// <inheritdoc />
    protected override void BuildWriteBitsRequest(McAddress address, int offset, ReadOnlySpan<bool> values, IBufferWriter<byte> output)
        => McFrameOutput.Write(exchange.BatchWriteBits(address.Device, address.Number + offset, values), output);

    /// <inheritdoc />
    protected override CommResult ParseWriteResponse(ReadOnlySpan<byte> frame) => exchange.ReadWrite(frame);

    /// <summary>
    /// 检查整个操作的末端软元件编号：字访问位软元件时每字覆盖 16 个点；末端不得超过字段编码能表示的上限（ASCII 十进制为 999999）。
    /// 违反时返回 InvalidRequest。
    /// </summary>
    /// <inheritdoc />
    protected override CommResult ValidateRange(McAddress address, PlcAccess access, int count)
    {
        // 位访问只会以位软元件调用（字软元件的位读取由 Plc 层改为读字，P2-D10）。
        bool bitDevice = address.Device.Unit == PlcAddressUnit.Bit;
        int points = access == PlcAccess.Words && bitDevice ? McLimits.BitsPerWord * count : count;
        int last = address.Number + points - 1;
        int maximum = exchange.MaxDeviceNumber(address.Device);
        return last > maximum
            ? CommResult.Fail(
                $"The device range of '{address.Text}' ends at {last}, beyond the device number limit {maximum} for this encoding.",
                CommErrorKind.InvalidRequest)
            : CommResult.Success();
    }

    private int MaxPoints(PlcAccess access) => access == PlcAccess.Words ? McLimits.WordPoints : settings.MaxBitPoints;

    // 字访问位软元件时偏移以字计，每个字覆盖 16 个点（手册 p.72）；其余情况偏移以点计。
    private static int WordHead(McAddress address, int offset)
        => address.Device.Unit == PlcAddressUnit.Bit
            ? address.Number + McLimits.BitsPerWord * offset
            : address.Number + offset;
}
