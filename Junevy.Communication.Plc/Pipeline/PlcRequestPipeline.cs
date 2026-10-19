using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 公开操作的流水线。每个操作依次经过：入口校验（地址、数量、数据、<c>ValidateRange</c>）→ 释放检查 → 未连接立即返回 → 操作锁 → 设备级传输。
/// 协议钩子是外部实现，它的任何异常都在这里转换为 Unspecified 失败并记录（P2-D14），不让异常逃出公开方法。
/// 按访问粒度分成几个部分文件：本文件是入口与守卫，字、字节、位操作各占一个文件。
/// </summary>
internal sealed partial class PlcRequestPipeline<TAddress> where TAddress : PlcAddress
{
    private const string ReadOperation = "ReadAsync";
    private const string WriteOperation = "WriteAsync";
    private const string ReadBytesOperation = "ReadBytesAsync";
    private const string WriteBytesOperation = "WriteBytesAsync";
    private const string ReadBitsOperation = "ReadBitsAsync";
    private const string WriteBitsOperation = "WriteBitsAsync";
    private const string CountMessage = "The count must be at least 1.";
    private const string TooLargeMessage = "The count is too large.";
    private const string EmptyDataMessage = "The data must not be empty.";
    private const string DisposedMessage = "The PLC client has been disposed.";
    private const string NotConnectedMessage = "The channel is not connected.";

    private readonly PlcClientBase<TAddress> client;
    private readonly IClientChannel channel;
    private readonly PlcPipelineSettings settings;
    private readonly WordOrder wordOrder;
    private readonly ILogger logger;
    private readonly PlcPacketExchange exchange;
    private readonly PlcTransfer<TAddress> transfer;

    // 同一客户端内的操作串行执行：包括拆包的全部分包，以及读-改-写的读与写。
    // SemaphoreSlim 不访问 AvailableWaitHandle，不持有非托管句柄；与 StreamChannel 的请求锁一样不释放。
    private readonly SemaphoreSlim operationLock = new SemaphoreSlim(1, 1);
    private int disposed;

    /// <summary>
    /// 初始化流水线。
    /// </summary>
    /// <param name="client">协议客户端（协议钩子的所有者）。</param>
    /// <param name="channel">底层通道（客户端拥有它，流水线只使用它的连接状态与请求）。</param>
    /// <param name="settings">校验过的选项快照。</param>
    /// <param name="dataFormat">数据格式（字序用于位访问）。</param>
    /// <param name="logger">日志记录器。</param>
    public PlcRequestPipeline(PlcClientBase<TAddress> client, IClientChannel channel, PlcPipelineSettings settings,
                              PlcDataFormat dataFormat, ILogger logger)
    {
        this.client = client;
        this.channel = channel;
        this.settings = settings;
        wordOrder = dataFormat.WordOrder;
        this.logger = logger;
        exchange = new PlcPacketExchange(channel, settings, logger);
        transfer = new PlcTransfer<TAddress>(client, exchange);
    }

    /// <summary>
    /// 标记为已释放。只有第一次调用返回 true（调用方据此释放通道）；之后的公开操作返回 <c>NotConnected</c>。
    /// </summary>
    /// <returns>第一次标记时返回 true。</returns>
    public bool TryMarkDisposed() => Interlocked.Exchange(ref disposed, 1) == 0;

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
        if (client.TryParseAddress(address ?? string.Empty, out target, out string? error))
        {
            failure = null;
            return true;
        }

        failure = CommResult.Fail(error!, CommErrorKind.InvalidRequest);
        return false;
    }

    // 写入的入口：先解析地址，再拒绝只读地址（P7-S1）。拒绝发生在点数检查之前，因此不发请求，也不进入操作锁。
    private bool TryResolveWritable(string address, [NotNullWhen(true)] out TAddress? target, [NotNullWhen(false)] out CommResult? failure)
    {
        if (!TryResolve(address, out target, out failure))
            return false;
        failure = CheckWritable(target);
        if (failure is not null)
            return false;
        return true;
    }

    // 只读地址的写入（P7-S1）：在点数检查之前拒绝，因此不发请求，也不进入操作锁。
    private CommResult? CheckWritable(TAddress target)
        => client.IsReadOnly(target)
            ? CommResult.Fail($"The address '{target.Text}' is read-only.", CommErrorKind.InvalidRequest)
            : null;

    // 位访问的入口检查：点数是否允许由协议的 ValidateRange 决定。
    private CommResult? CheckBitAccess(TAddress target, int points) => Rejection(client.ValidateRange(target, PlcAccess.Bits, points));

    // 字访问的入口检查：字数必须在范围内，再由协议的 ValidateRange 决定是否允许。
    private CommResult? CheckWordAccess(TAddress target, int words)
    {
        if (words < 1)
            return CommResult.Fail(CountMessage, CommErrorKind.InvalidRequest);
        if (words > PlcLayout.MaxWordCount)
            return CommResult.Fail(TooLargeMessage, CommErrorKind.InvalidRequest);

        return Rejection(client.ValidateRange(target, PlcAccess.Words, words));
    }

    // 字节访问的入口检查：上限按字节计（与字数上限同理，防止推导出的字数溢出），再由协议的 ValidateRange 决定。
    private CommResult? CheckByteAccess(TAddress target, int bytes)
    {
        if (bytes < 1)
            return CommResult.Fail(CountMessage, CommErrorKind.InvalidRequest);
        if (bytes > PlcLayout.MaxByteCount)
            return CommResult.Fail(TooLargeMessage, CommErrorKind.InvalidRequest);

        return Rejection(client.ValidateRange(target, PlcAccess.Bytes, bytes));
    }

    private static CommResult? Rejection(CommResult range) => range.IsSuccess ? null : range;

    private static CommResult<T> Invalid<T>(string message) => CommResult<T>.Fail(message, CommErrorKind.InvalidRequest);

    private static CommResult<bool> Acknowledge(CommResult result)
        => result.IsSuccess ? CommResult<bool>.Success(true) : result.As<bool>();
}
