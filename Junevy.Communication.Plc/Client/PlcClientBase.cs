using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc.Pipeline;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc;

/// <summary>
/// 地址型 PLC 客户端的基类。派生类只实现协议钩子（协议差异的全部）；连接与重连、超时、拆包、重试、位访问、
/// 操作锁与释放由套件提供。客户端拥有通道：释放客户端时同时释放通道。
/// </summary>
/// <remarks>
/// 公开操作不抛出运行期异常：参数错误返回 <c>InvalidRequest</c>，调用方取消返回 <c>Cancelled</c>，协议钩子的异常转换为 <c>Unspecified</c> 并记录。
/// 同一实例上的公开操作串行执行（包括拆包的全部分包与读-改-写）。
/// </remarks>
/// <typeparam name="TAddress">协议的地址类型。</typeparam>
public abstract class PlcClientBase<TAddress> : IPlcClient where TAddress : PlcAddress
{
    private readonly PlcRequestPipeline<TAddress> pipeline;

    /// <summary>
    /// 初始化客户端。
    /// </summary>
    /// <param name="channel">底层字节通道；客户端拥有它。</param>
    /// <param name="options">协议选项；构造时校验并取快照（D5），之后修改选项对象不影响本客户端。</param>
    /// <param name="dataFormat">数据格式（字序与字符串编码）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="PlcClientOptions.RetryCount"/> 或 <see cref="PlcClientOptions.RetryInterval"/> 为负。</exception>
    protected PlcClientBase(IClientChannel channel, PlcClientOptions options, PlcDataFormat dataFormat, ILogger logger)
    {
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        DataFormat = dataFormat ?? throw new ArgumentNullException(nameof(dataFormat));

        var settings = new PlcPipelineSettings(options ?? throw new ArgumentNullException(nameof(options)));
        pipeline = new PlcRequestPipeline<TAddress>(new ProtocolHooks(this), channel, settings, dataFormat, logger);
    }

    /// <inheritdoc />
    public string Name => Channel.Name;

    /// <inheritdoc />
    public ConnectionState State => Channel.State;

    /// <inheritdoc />
    public bool IsConnected => Channel.IsConnected;

    /// <inheritdoc />
    public ConnectionStatistics Statistics => Channel.Statistics;

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged
    {
        add => Channel.StateChanged += value;
        remove => Channel.StateChanged -= value;
    }

    /// <inheritdoc />
    public Task<CommResult> ConnectAsync(CancellationToken cancellationToken = default) => Channel.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Channel.DisconnectAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> WaitForConnectedAsync(int timeout, CancellationToken cancellationToken = default)
        => Channel.WaitForConnectedAsync(timeout, cancellationToken);

    /// <inheritdoc />
    public PlcDataFormat DataFormat { get; }

    /// <summary>底层字节通道。派生类可以直接使用它（例如协议自定义的握手），但不应绕过套件的操作锁发送请求。</summary>
    protected IClientChannel Channel { get; }

    /// <summary>日志记录器。</summary>
    protected ILogger Logger { get; }

    // —— 协议必须实现：这就是"协议差异"的全部 ——

    /// <summary>
    /// 解析地址文本。失败时返回英文错误消息，流水线将其转换为 <c>InvalidRequest</c>。
    /// </summary>
    /// <param name="address">地址文本（可能为空字符串）。</param>
    /// <param name="parsed">解析成功时为地址。</param>
    /// <param name="error">解析失败时为错误消息。</param>
    /// <returns>解析成功返回 true。</returns>
    protected abstract bool TryParseAddress(string address, [NotNullWhen(true)] out TAddress? parsed, [NotNullWhen(false)] out string? error);

    /// <summary>
    /// 一包读取的最大点数。<paramref name="access"/> 为 <c>Words</c> 时按字计，为 <c>Bits</c> 时按点计。返回值必须 ≥ 1。
    /// </summary>
    /// <param name="address">已解析的地址。</param>
    /// <param name="access">访问粒度。</param>
    /// <returns>单包最大点数。</returns>
    protected abstract int GetMaxReadPoints(TAddress address, PlcAccess access);

    /// <summary>
    /// 一包写入的最大点数，含义同 <see cref="GetMaxReadPoints"/>。返回值必须 ≥ 1。
    /// </summary>
    /// <param name="address">已解析的地址。</param>
    /// <param name="access">访问粒度。</param>
    /// <returns>单包最大点数。</returns>
    protected abstract int GetMaxWritePoints(TAddress address, PlcAccess access);

    /// <summary>
    /// 写入读字请求。<paramref name="offset"/> 是相对于 <c>address.Number</c> 的偏移，单位与访问一致；协议据此计算本包的起始编号。
    /// </summary>
    /// <param name="address">已解析的地址（协议只使用编号与软元件，忽略位号）。</param>
    /// <param name="offset">本包相对于操作起点的偏移。</param>
    /// <param name="count">本包的字数。</param>
    /// <param name="output">请求字节的写入目标。</param>
    protected abstract void BuildReadWordsRequest(TAddress address, int offset, int count, IBufferWriter<byte> output);

    /// <summary>
    /// 解析读字应答。成功时返回 <paramref name="count"/> 个字，即 <c>count × 2</c> 字节（规范化布局，见 <see cref="IPlcClient.ReadAsync"/>）。
    /// </summary>
    /// <param name="count">本包请求的字数。</param>
    /// <param name="frame">应答帧。</param>
    /// <returns>解析结果；对端错误返回 <c>RemoteError</c>。</returns>
    protected abstract CommResult<byte[]> ParseReadWordsResponse(int count, ReadOnlySpan<byte> frame);

    /// <summary>
    /// 写入写字请求。<paramref name="words"/> 是本包的规范化字节，每字 2 字节。
    /// </summary>
    /// <param name="address">已解析的地址。</param>
    /// <param name="offset">本包相对于操作起点的偏移。</param>
    /// <param name="words">本包的字节。</param>
    /// <param name="output">请求字节的写入目标。</param>
    protected abstract void BuildWriteWordsRequest(TAddress address, int offset, ReadOnlySpan<byte> words, IBufferWriter<byte> output);

    /// <summary>写入读位请求，含义同 <see cref="BuildReadWordsRequest"/>；位访问的 <paramref name="count"/> 按点计。</summary>
    /// <param name="address">已解析的地址。</param>
    /// <param name="offset">本包相对于操作起点的偏移（点）。</param>
    /// <param name="count">本包的点数。</param>
    /// <param name="output">请求字节的写入目标。</param>
    protected abstract void BuildReadBitsRequest(TAddress address, int offset, int count, IBufferWriter<byte> output);

    /// <summary>解析读位应答。成功时返回 <paramref name="count"/> 个点的值。</summary>
    /// <param name="count">本包请求的点数。</param>
    /// <param name="frame">应答帧。</param>
    /// <returns>解析结果。</returns>
    protected abstract CommResult<bool[]> ParseReadBitsResponse(int count, ReadOnlySpan<byte> frame);

    /// <summary>写入写位请求，含义同 <see cref="BuildWriteWordsRequest"/>。</summary>
    /// <param name="address">已解析的地址。</param>
    /// <param name="offset">本包相对于操作起点的偏移（点）。</param>
    /// <param name="values">本包的点值。</param>
    /// <param name="output">请求字节的写入目标。</param>
    protected abstract void BuildWriteBitsRequest(TAddress address, int offset, ReadOnlySpan<bool> values, IBufferWriter<byte> output);

    /// <summary>解析写入应答（字写与位写共用）。</summary>
    /// <param name="frame">应答帧。</param>
    /// <returns>成功返回 <see cref="CommResult.Success()"/>；对端错误返回 <c>RemoteError</c>。</returns>
    protected abstract CommResult ParseWriteResponse(ReadOnlySpan<byte> frame);

    // —— 协议按需覆写 ——

    /// <summary>
    /// 协议对地址与点数的额外限制（例如某个软元件的访问范围）。在任何请求发出之前调用；返回失败时操作直接返回该失败。
    /// </summary>
    /// <param name="address">已解析的地址。</param>
    /// <param name="access">访问粒度。</param>
    /// <param name="count">字数或点数（按 <paramref name="access"/>）。</param>
    /// <returns>允许时返回成功结果。</returns>
    protected virtual CommResult ValidateRange(TAddress address, PlcAccess access, int count) => CommResult.Success();

    // —— IPlcClient ——

    /// <inheritdoc />
    public Task<CommResult<byte[]>> ReadAsync(string address, int count, CancellationToken cancellationToken = default)
        => pipeline.ReadWordsAsync(address, count, cancellationToken);

    /// <inheritdoc />
    public Task<CommResult> WriteAsync(string address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => pipeline.WriteWordsAsync(address, data, cancellationToken);

    /// <inheritdoc />
    public Task<CommResult<bool[]>> ReadBitsAsync(string address, int count, CancellationToken cancellationToken = default)
        => pipeline.ReadBitsAsync(address, count, cancellationToken);

    /// <inheritdoc />
    public Task<CommResult> WriteBitsAsync(string address, ReadOnlyMemory<bool> values, CancellationToken cancellationToken = default)
        => pipeline.WriteBitsAsync(address, values, cancellationToken);

    /// <summary>
    /// 释放客户端及其通道（P2-D13）。释放之后的公开操作返回 <c>NotConnected</c>。可重复调用。
    /// </summary>
    public void Dispose()
    {
        if (pipeline.TryMarkDisposed())
            Channel.Dispose();
    }

    /// <summary>
    /// 异步释放客户端及其通道（P2-D13）。释放之后的公开操作返回 <c>NotConnected</c>。可重复调用。
    /// </summary>
    /// <returns>释放完成的任务。</returns>
    public ValueTask DisposeAsync() => pipeline.TryMarkDisposed() ? Channel.DisposeAsync() : default;

    // 把 protected 钩子转交给流水线。流水线只依赖 IPlcProtocol，因此不访问客户端的 protected 成员，也就不依赖客户端类型。
    private sealed class ProtocolHooks : IPlcProtocol<TAddress>
    {
        private readonly PlcClientBase<TAddress> owner;

        public ProtocolHooks(PlcClientBase<TAddress> owner) => this.owner = owner;

        public bool TryParseAddress(string address, [NotNullWhen(true)] out TAddress? parsed, [NotNullWhen(false)] out string? error)
            => owner.TryParseAddress(address, out parsed, out error);

        public CommResult ValidateRange(TAddress address, PlcAccess access, int count) => owner.ValidateRange(address, access, count);

        public int GetMaxReadPoints(TAddress address, PlcAccess access) => owner.GetMaxReadPoints(address, access);

        public int GetMaxWritePoints(TAddress address, PlcAccess access) => owner.GetMaxWritePoints(address, access);

        public void BuildReadWordsRequest(TAddress address, int offset, int count, IBufferWriter<byte> output)
            => owner.BuildReadWordsRequest(address, offset, count, output);

        public CommResult<byte[]> ParseReadWordsResponse(int count, byte[] frame) => owner.ParseReadWordsResponse(count, frame);

        public void BuildWriteWordsRequest(TAddress address, int offset, ReadOnlyMemory<byte> words, IBufferWriter<byte> output)
            => owner.BuildWriteWordsRequest(address, offset, words.Span, output);

        public void BuildReadBitsRequest(TAddress address, int offset, int count, IBufferWriter<byte> output)
            => owner.BuildReadBitsRequest(address, offset, count, output);

        public CommResult<bool[]> ParseReadBitsResponse(int count, byte[] frame) => owner.ParseReadBitsResponse(count, frame);

        public void BuildWriteBitsRequest(TAddress address, int offset, ReadOnlyMemory<bool> values, IBufferWriter<byte> output)
            => owner.BuildWriteBitsRequest(address, offset, values.Span, output);

        public CommResult ParseWriteResponse(byte[] frame) => owner.ParseWriteResponse(frame);
    }
}
