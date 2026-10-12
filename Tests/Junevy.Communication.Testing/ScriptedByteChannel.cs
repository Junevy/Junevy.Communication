using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Testing;

/// <summary>
/// 脚本化的字节通道：每一帧出站数据都按回调得到应答或失败，并按顺序记录在 <see cref="Requests"/>。
/// 用于测试"发一帧、等一帧"的逻辑（握手步骤、心跳探测）；不涉及真实链路，因此没有超时、分帧与并发。
/// </summary>
public sealed class ScriptedByteChannel : IByteChannel
{
    private readonly Func<byte[], CommResult<byte[]>> responder;
    private readonly List<byte[]> requests = new List<byte[]>();

    /// <summary>创建脚本化通道。</summary>
    /// <param name="responder">根据出站帧给出应答或失败结果。</param>
    /// <exception cref="ArgumentNullException"><paramref name="responder"/> 为 null。</exception>
    public ScriptedByteChannel(Func<byte[], CommResult<byte[]>> responder)
        => this.responder = responder ?? throw new ArgumentNullException(nameof(responder));

    /// <summary>已发出的帧，按顺序（<c>RequestAsync</c> 与 <c>SendAsync</c> 都记录）。</summary>
    public IReadOnlyList<byte[]> Requests => requests;

    /// <summary>最近一次 <see cref="RequestAsync"/> 收到的单次请求选项；协议使用通道的默认超时时为 null。</summary>
    public RequestOptions? LastOptions { get; private set; }

    /// <summary>
    /// 最近一次 <see cref="RequestAsync"/> 发出的帧；还没有请求过（或只调用过 <see cref="SendAsync"/>）时为 null。
    /// <see cref="SendAsync"/> 不更新它：纯发送的协议（帧同步）没有"请求-应答"可言。
    /// </summary>
    public byte[]? LastRequest { get; private set; }

    /// <inheritdoc />
    public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        Record(payload);
        return Task.FromResult(CommResult.Success());
    }

    /// <inheritdoc />
    public Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastRequest = Record(payload);
        LastOptions = options;
        return Task.FromResult(responder(LastRequest));
    }

    /// <inheritdoc />
    public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(CommResult<byte[]>.Fail("The scripted channel does not receive unsolicited frames.", CommErrorKind.NotSupported));

    /// <inheritdoc />
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived
    {
        add { }
        remove { }
    }

    private byte[] Record(ReadOnlyMemory<byte> payload)
    {
        byte[] frame = payload.ToArray();
        requests.Add(frame);
        return frame;
    }
}
