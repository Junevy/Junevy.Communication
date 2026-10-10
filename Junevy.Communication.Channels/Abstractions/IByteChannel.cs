using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Channels;

/// <summary>
/// 字节收发能力。TCP 客户端、TCP 服务端会话、UDP、串口都实现它。
/// 链路状态（<c>IsConnected</c>）只由 <see cref="IConnectable"/> 声明，本接口不重复声明；
/// 否则同时继承两者的 <see cref="IClientChannel"/> 访问 <c>IsConnected</c> 时会产生歧义（CS0229）。
/// </summary>
public interface IByteChannel
{
    /// <summary>发送一帧（整帧写出，受 SendTimeout 约束）。</summary>
    /// <param name="payload">负载（分帧器的编码器会在此基础上追加分隔符等）。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>发送结果。</returns>
    Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>发送一帧并等待匹配的应答帧。</summary>
    /// <param name="payload">请求负载。</param>
    /// <param name="options">单次请求选项；为 null 时使用配置默认值。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>应答帧或失败结果。</returns>
    Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>不发送，只等待下一个匹配帧（例如等设备主动上报 "READY"）。</summary>
    /// <param name="options">单次接收选项；为 null 时使用配置默认值。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>匹配的帧或失败结果。</returns>
    Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>未被 <see cref="RequestAsync"/> / <see cref="ReceiveAsync"/> 认领的入站帧。</summary>
    event EventHandler<FrameReceivedEventArgs>? FrameReceived;
}
