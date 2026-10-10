using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 内置心跳探测：发送心跳负载；若指定了期望应答，则要求收到的应答与之逐字节相等（D14）。
/// 未指定期望应答时，发送成功即视为健康。
/// </summary>
internal sealed class PayloadHeartbeatProbe : IHealthProbe
{
    private readonly IByteChannel channel;
    private readonly byte[] payload;
    private readonly byte[]? expectedReply;
    private readonly int timeout;

    /// <summary>
    /// 创建内置心跳探测。
    /// </summary>
    /// <param name="channel">执行发送与请求的字节通道。</param>
    /// <param name="payload">心跳负载，不能为空。</param>
    /// <param name="expectedReply">期望的应答（精确匹配）；为 null 时只要求发送成功。</param>
    /// <param name="timeout">等待应答的超时（毫秒），必须为正。</param>
    /// <exception cref="ArgumentNullException">通道或负载为 null。</exception>
    /// <exception cref="ArgumentException">负载或期望应答为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">超时不为正。</exception>
    public PayloadHeartbeatProbe(IByteChannel channel, byte[] payload, byte[]? expectedReply, int timeout)
    {
        if (channel == null)
            throw new ArgumentNullException(nameof(channel));
        if (payload == null)
            throw new ArgumentNullException(nameof(payload));
        if (payload.Length == 0)
            throw new ArgumentException("The heartbeat payload must not be empty.", nameof(payload));
        if (expectedReply != null && expectedReply.Length == 0)
            throw new ArgumentException("The expected heartbeat reply must not be empty when it is specified.", nameof(expectedReply));
        if (timeout <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The heartbeat timeout must be positive.");

        this.channel = channel;
        this.payload = (byte[])payload.Clone();
        this.expectedReply = expectedReply == null ? null : (byte[])expectedReply.Clone();
        this.timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<CommResult> ProbeAsync(CancellationToken cancellationToken)
    {
        if (expectedReply == null)
            return await channel.SendAsync(payload, cancellationToken).ConfigureAwait(false);

        CommResult<byte[]> reply = await channel.RequestAsync(payload, new RequestOptions { Timeout = timeout }, cancellationToken)
            .ConfigureAwait(false);
        if (!reply.IsSuccess)
            return reply.ToResult();

        if (!(reply.Data ?? Array.Empty<byte>()).SequenceEqual(expectedReply))
            return CommResult.Fail("The heartbeat reply does not match the expected reply.", CommErrorKind.ProtocolViolation);

        return CommResult.Success();
    }
}
