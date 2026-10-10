using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 脚本化的字节通道：记录最近一次请求的帧，并由 <c>respond</c> 给出应答（或失败）。用于心跳探测等单元测试。
/// </summary>
internal sealed class FakeByteChannel : IByteChannel
{
    private readonly Func<byte[], CommResult<byte[]>> respond;

    /// <summary>创建脚本化通道。</summary>
    /// <param name="respond">根据请求帧给出应答。</param>
    public FakeByteChannel(Func<byte[], CommResult<byte[]>> respond)
    {
        this.respond = respond;
    }

    /// <summary>最近一次请求的帧。</summary>
    public byte[]? LastRequest { get; private set; }

    /// <inheritdoc />
    public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => Task.FromResult(CommResult.Success());

    /// <inheritdoc />
    public Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastRequest = payload.ToArray();
        return Task.FromResult(respond(LastRequest));
    }

    /// <inheritdoc />
    public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(CommResult<byte[]>.Fail("The fake channel does not receive unsolicited frames.", CommErrorKind.NotSupported));

    /// <inheritdoc />
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived
    {
        add { }
        remove { }
    }
}
