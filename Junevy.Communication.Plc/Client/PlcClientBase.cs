using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc.Pipeline;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc;

/// <summary>
/// 地址型 PLC 客户端的基类。派生类只实现协议钩子（协议差异的全部，集中在这个类的 Hooks 部分）；连接与重连、超时、拆包、重试、位访问、
/// 操作锁与释放由套件提供。客户端拥有通道：释放客户端时同时释放通道。
/// </summary>
/// <remarks>
/// 公开操作不抛出运行期异常：参数错误返回 <c>InvalidRequest</c>，调用方取消返回 <c>Cancelled</c>，协议钩子的异常转换为 <c>Unspecified</c> 并记录。
/// 同一实例上的公开操作串行执行（包括拆包的全部分包与读-改-写）。
/// </remarks>
/// <typeparam name="TAddress">协议的地址类型。</typeparam>
public abstract partial class PlcClientBase<TAddress> : IPlcClient where TAddress : PlcAddress
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
        pipeline = new PlcRequestPipeline<TAddress>(this, channel, settings, dataFormat, logger);
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

    /// <inheritdoc />
    public Task<CommResult<byte[]>> ReadBytesAsync(string address, int count, CancellationToken cancellationToken = default)
        => pipeline.ReadBytesAsync(address, count, cancellationToken);

    /// <inheritdoc />
    public Task<CommResult> WriteBytesAsync(string address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => pipeline.WriteBytesAsync(address, data, cancellationToken);

    /// <inheritdoc />
    public CommResult<PlcAddress> ParseAddress(string address)
        => TryParseAddress(address ?? string.Empty, out TAddress? parsed, out string? error)
            ? CommResult<PlcAddress>.Success(parsed)
            : CommResult<PlcAddress>.Fail(error!, CommErrorKind.InvalidRequest);

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
}
