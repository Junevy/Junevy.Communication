using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 流水线的位访问（<c>ReadBitsAsync</c> / <c>WriteBitsAsync</c>）：位软元件走协议的位钩子，
/// 字软元件按字读取或读-改-写（P2-D10）。
/// </summary>
internal partial class PlcRequestPipeline<TAddress> where TAddress : PlcAddress
{
    /// <summary>读取位，见 <c>IPlcClient.ReadBitsAsync</c>。</summary>
    public Task<CommResult<bool[]>> ReadBitsAsync(string address, int count, CancellationToken cancellationToken)
        => GuardAsync(ReadBitsOperation, cancellationToken, () => ReadBitsCoreAsync(address, count, cancellationToken));

    /// <summary>写入位，见 <c>IPlcClient.WriteBitsAsync</c>。</summary>
    public async Task<CommResult> WriteBitsAsync(string address, ReadOnlyMemory<bool> values, CancellationToken cancellationToken)
    {
        CommResult<bool> result = await GuardAsync(WriteBitsOperation, cancellationToken, () => WriteBitsCoreAsync(address, values, cancellationToken))
            .ConfigureAwait(false);
        return result.ToResult();
    }

    private async Task<CommResult<bool[]>> ReadBitsCoreAsync(string address, int count, CancellationToken cancellationToken)
    {
        if (!TryResolve(address, out TAddress? target, out CommResult? failure))
            return failure.As<bool[]>();
        if (count < 1)
            return Invalid<bool[]>(CountMessage);

        if (target.Unit == PlcAddressUnit.Bit)
        {
            CommResult? bitRejected = CheckBitAccess(target, count);
            if (bitRejected is not null)
                return bitRejected.As<bool[]>();

            return await ExclusiveAsync(cancellationToken, () => transfer.ReadBitsAsync(ReadBitsOperation, target, count, cancellationToken))
                .ConfigureAwait(false);
        }

        // 字软元件：读取覆盖这些点的字，再从低位起取位。
        int bitStart = target.BitIndex ?? 0;
        int words = PlcBitPacking.WordCount(bitStart, count);
        CommResult? rejected = CheckWordAccess(target, words);
        if (rejected is not null)
            return rejected.As<bool[]>();

        return await ExclusiveAsync(cancellationToken, async () =>
        {
            CommResult<byte[]> read = await transfer.ReadWordsAsync(ReadBitsOperation, target, words, cancellationToken).ConfigureAwait(false);
            return read.IsSuccess
                ? CommResult<bool[]>.Success(PlcBitPacking.Extract(read.Data!, wordOrder, bitStart, count))
                : read.As<bool[]>();
        }).ConfigureAwait(false);
    }

    private async Task<CommResult<bool>> WriteBitsCoreAsync(string address, ReadOnlyMemory<bool> values, CancellationToken cancellationToken)
    {
        if (!TryResolve(address, out TAddress? target, out CommResult? failure))
            return failure.As<bool>();
        if (values.Length < 1)
            return Invalid<bool>("The value list must not be empty.");

        if (target.Unit == PlcAddressUnit.Bit)
        {
            CommResult? bitRejected = CheckBitAccess(target, values.Length);
            if (bitRejected is not null)
                return bitRejected.As<bool>();

            return await ExclusiveAsync(cancellationToken, async () => Acknowledge(
                await transfer.WriteBitsAsync(WriteBitsOperation, target, values, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
        }

        if (!settings.AllowReadModifyWriteBits)
        {
            return CommResult<bool>.Fail("Writing single bits of a word device needs PlcClientOptions.AllowReadModifyWriteBits.",
                                         CommErrorKind.NotSupported);
        }

        int bitStart = target.BitIndex ?? 0;
        int words = PlcBitPacking.WordCount(bitStart, values.Length);
        CommResult? rejected = CheckWordAccess(target, words);
        if (rejected is not null)
            return rejected.As<bool>();

        return await ExclusiveAsync(cancellationToken, () => ReadModifyWriteAsync(target, bitStart, words, values, cancellationToken))
            .ConfigureAwait(false);
    }

    // 读-改-写：在操作锁内读取覆盖的字，修改目标位，再整体写回；每次都记录 Warning（P2-D10）。
    private async Task<CommResult<bool>> ReadModifyWriteAsync(TAddress target, int bitStart, int words, ReadOnlyMemory<bool> values,
                                                             CancellationToken cancellationToken)
    {
        logger.LogWarning("Writing {Points} bit(s) of word device '{Address}' by read-modify-write; {Words} word(s) are read and written back, "
                          + "so changes made by the PLC program in between are overwritten.", values.Length, target.Text, words);

        CommResult<byte[]> read = await transfer.ReadWordsAsync(WriteBitsOperation, target, words, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
            return read.As<bool>();

        PlcBitPacking.Insert(read.Data!, wordOrder, bitStart, values.Span);
        return Acknowledge(await transfer.WriteWordsAsync(WriteBitsOperation, target, read.Data!, cancellationToken).ConfigureAwait(false));
    }
}
