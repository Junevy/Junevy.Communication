using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 流水线的字节访问（<c>ReadBytesAsync</c> / <c>WriteBytesAsync</c>）：字节软元件按字节精确访问（走协议的字节钩子），
/// 字与位软元件按字访问——读取覆盖这些字节的整字后截断，写入只允许整字（P4-D4）。
/// </summary>
internal partial class PlcRequestPipeline<TAddress> where TAddress : PlcAddress
{
    private const string WholeWordsMessage =
        "Byte access on a word or bit device can only write whole words; the data length must be a positive even number.";

    /// <summary>按字节读取，见 <c>IPlcClient.ReadBytesAsync</c>。</summary>
    public Task<CommResult<byte[]>> ReadBytesAsync(string address, int count, CancellationToken cancellationToken)
        => GuardAsync(ReadBytesOperation, cancellationToken, () => ReadBytesCoreAsync(address, count, cancellationToken));

    /// <summary>按字节写入，见 <c>IPlcClient.WriteBytesAsync</c>。</summary>
    public async Task<CommResult> WriteBytesAsync(string address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        CommResult<bool> result = await GuardAsync(WriteBytesOperation, cancellationToken, () => WriteBytesCoreAsync(address, data, cancellationToken))
            .ConfigureAwait(false);
        return result.ToResult();
    }

    private async Task<CommResult<byte[]>> ReadBytesCoreAsync(string address, int count, CancellationToken cancellationToken)
    {
        if (!TryResolve(address, out TAddress? target, out CommResult? failure))
            return failure.As<byte[]>();
        if (target.BitIndex is not null)
            return Invalid<byte[]>("A bit index is not allowed in ReadBytesAsync; use ReadBitsAsync to read single bits.");
        if (count < 1)
            return Invalid<byte[]>(CountMessage);

        if (target.Unit == PlcAddressUnit.Byte)
        {
            CommResult? byteRejected = CheckByteAccess(target, count);
            if (byteRejected is not null)
                return byteRejected.As<byte[]>();

            return await ExclusiveAsync(cancellationToken, () => transfer.ReadBytesAsync(ReadBytesOperation, target, count, cancellationToken))
                .ConfigureAwait(false);
        }

        int words = PlcLayout.WordsFor(count);
        CommResult? rejected = CheckWordAccess(target, words);
        if (rejected is not null)
            return rejected.As<byte[]>();

        return await ExclusiveAsync(cancellationToken, async () =>
        {
            CommResult<byte[]> read = await transfer.ReadWordsAsync(ReadBytesOperation, target, words, cancellationToken).ConfigureAwait(false);
            return read.IsSuccess ? CommResult<byte[]>.Success(TakeBytes(read.Data, count)) : read.As<byte[]>();
        }).ConfigureAwait(false);
    }

    private async Task<CommResult<bool>> WriteBytesCoreAsync(string address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!TryResolve(address, out TAddress? target, out CommResult? failure))
            return failure.As<bool>();
        if (target.BitIndex is not null)
            return Invalid<bool>("A bit index is not allowed in WriteBytesAsync; use WriteBitsAsync to write single bits.");
        if (data.Length == 0)
            return Invalid<bool>(EmptyDataMessage);

        if (target.Unit == PlcAddressUnit.Byte)
            return await WriteBytesExactAsync(target, data, cancellationToken).ConfigureAwait(false);

        if (data.Length % PlcLayout.WordBytes != 0)
            return Invalid<bool>(WholeWordsMessage);

        CommResult? rejected = CheckWordAccess(target, data.Length / PlcLayout.WordBytes);
        if (rejected is not null)
            return rejected.As<bool>();

        return await ExclusiveAsync(cancellationToken, async () => Acknowledge(
            await transfer.WriteWordsAsync(WriteBytesOperation, target, data, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }

    // 字节软元件的写入：字节数即点数，上限按字节计。
    private async Task<CommResult<bool>> WriteBytesExactAsync(TAddress target, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        CommResult? rejected = CheckByteAccess(target, data.Length);
        if (rejected is not null)
            return rejected.As<bool>();

        return await ExclusiveAsync(cancellationToken, async () => Acknowledge(
            await transfer.WriteBytesAsync(WriteBytesOperation, target, data, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }

    // 整字读取的结果可能比请求多半个字节：只取前面的 count 个字节。
    private static byte[] TakeBytes(byte[] data, int count) => data.AsSpan(0, count).ToArray();
}
