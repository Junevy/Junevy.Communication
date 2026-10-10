using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 公开操作的流水线。每个操作依次经过：入口校验（地址、数量、数据、<c>ValidateRange</c>）→ 释放检查 → 未连接立即返回 → 操作锁 → 设备级传输。
/// 协议钩子是外部实现，它的任何异常都在这里转换为 Unspecified 失败并记录（P2-D14），不让异常逃出公开方法。
/// </summary>
internal sealed class PlcRequestPipeline<TAddress> where TAddress : PlcAddress
{
    private const string ReadOperation = "ReadAsync";
    private const string WriteOperation = "WriteAsync";
    private const string ReadBitsOperation = "ReadBitsAsync";
    private const string WriteBitsOperation = "WriteBitsAsync";
    private const string CountMessage = "The count must be at least 1.";
    private const string DisposedMessage = "The PLC client has been disposed.";
    private const string NotConnectedMessage = "The channel is not connected.";

    private readonly IPlcProtocol<TAddress> protocol;
    private readonly IClientChannel channel;
    private readonly PlcPipelineSettings settings;
    private readonly WordOrder wordOrder;
    private readonly ILogger logger;
    private readonly PlcTransfer<TAddress> transfer;

    // 同一客户端内的操作串行执行：包括拆包的全部分包，以及读-改-写的读与写。
    // SemaphoreSlim 不访问 AvailableWaitHandle，不持有非托管句柄；与 StreamChannel 的请求锁一样不释放。
    private readonly SemaphoreSlim operationLock = new SemaphoreSlim(1, 1);
    private int disposed;

    /// <summary>
    /// 初始化流水线。
    /// </summary>
    /// <param name="protocol">协议钩子。</param>
    /// <param name="channel">底层通道（客户端拥有它，流水线只使用它的连接状态与请求）。</param>
    /// <param name="settings">校验过的选项快照。</param>
    /// <param name="dataFormat">数据格式（字序用于位访问）。</param>
    /// <param name="logger">日志记录器。</param>
    public PlcRequestPipeline(IPlcProtocol<TAddress> protocol, IClientChannel channel, PlcPipelineSettings settings,
                              PlcDataFormat dataFormat, ILogger logger)
    {
        this.protocol = protocol;
        this.channel = channel;
        this.settings = settings;
        wordOrder = dataFormat.WordOrder;
        this.logger = logger;
        transfer = new PlcTransfer<TAddress>(protocol, new PlcPacketExchange(channel, settings, logger));
    }

    /// <summary>
    /// 标记为已释放。只有第一次调用返回 true（调用方据此释放通道）；之后的公开操作返回 <c>NotConnected</c>。
    /// </summary>
    /// <returns>第一次标记时返回 true。</returns>
    public bool TryMarkDisposed() => Interlocked.Exchange(ref disposed, 1) == 0;

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
        if (target.BitIndex is not null)
            return Invalid<bool>("A bit index is not allowed in WriteAsync; use WriteBitsAsync to write single bits.");
        if (data.Length == 0)
            return Invalid<bool>("The data must not be empty.");
        if (data.Length % PlcLayout.WordBytes != 0)
            return Invalid<bool>("The data length must be a multiple of 2 bytes.");

        CommResult? rejected = CheckWordAccess(target, data.Length / PlcLayout.WordBytes);
        if (rejected is not null)
            return rejected.As<bool>();

        return await ExclusiveAsync(cancellationToken, async () => Acknowledge(
            await transfer.WriteWordsAsync(WriteOperation, target, data, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
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

    // 释放检查 → 未连接立即返回（不发请求）→ 操作锁。失败结果不进入锁。
    private async Task<CommResult<T>> ExclusiveAsync<T>(CancellationToken cancellationToken, Func<Task<CommResult<T>>> body)
    {
        if (Volatile.Read(ref disposed) != 0)
            return CommResult<T>.Fail(DisposedMessage, CommErrorKind.NotConnected);
        if (!channel.IsConnected)
            return CommResult<T>.Fail(NotConnectedMessage, CommErrorKind.NotConnected);

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            operationLock.Release();
        }
    }

    // 边界：协议钩子与通道调用的任何异常都在这里收口。调用方取消返回 Cancelled；其余异常记录后返回 Unspecified。
    private async Task<CommResult<T>> GuardAsync<T>(string operation, CancellationToken cancellationToken, Func<Task<CommResult<T>>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommResult<T>.Fail("The operation was cancelled by the caller.", CommErrorKind.Cancelled);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The operation failed unexpectedly during {Operation}.", operation);
            return CommResult<T>.Fail($"The operation failed unexpectedly during {operation}.", CommErrorKind.Unspecified, null, ex);
        }
    }

    private bool TryResolve(string address, [NotNullWhen(true)] out TAddress? target, [NotNullWhen(false)] out CommResult? failure)
    {
        if (protocol.TryParseAddress(address ?? string.Empty, out target, out string? error))
        {
            failure = null;
            return true;
        }

        failure = CommResult.Fail(error!, CommErrorKind.InvalidRequest);
        return false;
    }

    // 位访问的入口检查：点数是否允许由协议的 ValidateRange 决定。
    private CommResult? CheckBitAccess(TAddress target, int points) => Rejection(protocol.ValidateRange(target, PlcAccess.Bits, points));

    // 字访问的入口检查：字数必须在范围内，再由协议的 ValidateRange 决定是否允许。
    private CommResult? CheckWordAccess(TAddress target, int words)
    {
        if (words < 1)
            return CommResult.Fail(CountMessage, CommErrorKind.InvalidRequest);
        if (words > PlcLayout.MaxWordCount)
            return CommResult.Fail("The count is too large.", CommErrorKind.InvalidRequest);

        return Rejection(protocol.ValidateRange(target, PlcAccess.Words, words));
    }

    private static CommResult? Rejection(CommResult range) => range.IsSuccess ? null : range;

    private static CommResult<T> Invalid<T>(string message) => CommResult<T>.Fail(message, CommErrorKind.InvalidRequest);

    private static CommResult<bool> Acknowledge(CommResult result)
        => result.IsSuccess ? CommResult<bool>.Success(true) : result.As<bool>();
}
