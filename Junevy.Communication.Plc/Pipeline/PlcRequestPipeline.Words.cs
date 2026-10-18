using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 流水线的字访问（<c>ReadAsync</c> / <c>WriteAsync</c>）：数据按规范化布局，每字 2 字节。
/// </summary>
internal partial class PlcRequestPipeline<TAddress> where TAddress : PlcAddress
{
    /// <summary>读取字，见 <c>IPlcClient.ReadAsync</c>。</summary>
    public Task<CommResult<byte[]>> ReadWordsAsync(string address, int count, CancellationToken cancellationToken)
        => GuardAsync(ReadOperation, cancellationToken, () => ReadWordsCoreAsync(address, count, cancellationToken));

    /// <summary>写入字，见 <c>IPlcClient.WriteAsync</c>。</summary>
    public async Task<CommResult> WriteWordsAsync(string address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        CommResult<bool> result = await GuardAsync(WriteOperation, cancellationToken, () => WriteWordsCoreAsync(address, data, cancellationToken))
            .ConfigureAwait(false);
        return result.ToResult();
    }

    private async Task<CommResult<byte[]>> ReadWordsCoreAsync(string address, int count, CancellationToken cancellationToken)
    {
        if (!TryResolve(address, out TAddress? target, out CommResult? failure))
            return failure.As<byte[]>();
        if (target.BitIndex is not null)
            return Invalid<byte[]>("A bit index is not allowed in ReadAsync; use ReadBitsAsync to read single bits.");

        CommResult? rejected = CheckWordAccess(target, count);
        if (rejected is not null)
            return rejected.As<byte[]>();

        return await ExclusiveAsync(cancellationToken, () => transfer.ReadWordsAsync(ReadOperation, target, count, cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<CommResult<bool>> WriteWordsCoreAsync(string address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!TryResolve(address, out TAddress? target, out CommResult? failure))
            return failure.As<bool>();
        CommResult? readOnly = CheckWritable(target);
        if (readOnly is not null)
            return readOnly.As<bool>();
        if (target.BitIndex is not null)
            return Invalid<bool>("A bit index is not allowed in WriteAsync; use WriteBitsAsync to write single bits.");
        if (data.Length == 0)
            return Invalid<bool>(EmptyDataMessage);
        if (data.Length % PlcLayout.WordBytes != 0)
            return Invalid<bool>("The data length must be a multiple of 2 bytes.");

        CommResult? rejected = CheckWordAccess(target, data.Length / PlcLayout.WordBytes);
        if (rejected is not null)
            return rejected.As<bool>();

        return await ExclusiveAsync(cancellationToken, async () => Acknowledge(
            await transfer.WriteWordsAsync(WriteOperation, target, data, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }
}
