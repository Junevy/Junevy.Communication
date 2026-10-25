using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels;

/// <summary>
/// 按界面配置的握手（计划 DA-D12）：连接后发送一段字节；若配置了期待的应答，则等待应答并比较。
/// 库在每次连接（包括每次重连）时都会执行它。
/// </summary>
internal sealed class ScriptedInitializer : IConnectionInitializer
{
    private readonly byte[] _send;
    private readonly byte[]? _expectedReply;
    private readonly int _replyTimeout;

    public ScriptedInitializer(byte[] send, byte[]? expectedReply, int replyTimeout)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (send.Length == 0)
        {
            throw new ArgumentException("The handshake must send at least one byte.", nameof(send));
        }

        if (replyTimeout <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(replyTimeout), replyTimeout, "The reply timeout must be positive.");
        }

        _send = send;
        _expectedReply = expectedReply;
        _replyTimeout = replyTimeout;
    }

    public async Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)
    {
        CommResult sent = await channel.SendAsync(_send, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess || _expectedReply is null)
        {
            return sent;
        }

        CommResult<byte[]> reply = await channel.ReceiveAsync(new RequestOptions { Timeout = _replyTimeout }, cancellationToken)
            .ConfigureAwait(false);
        if (!reply.IsSuccess)
        {
            return reply.ToResult();
        }

        return reply.Data!.AsSpan().SequenceEqual(_expectedReply)
            ? CommResult.Success()
            : CommResult.Fail("The handshake reply does not match the expected bytes.", CommErrorKind.ProtocolViolation);
    }
}
