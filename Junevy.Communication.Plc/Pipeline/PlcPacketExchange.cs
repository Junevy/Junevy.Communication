using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 包交换：发送一包并解析应答，按 P2-D7 逐包重试；把一次操作拆成多包依次执行，任一包失败即整体失败。
/// 只在操作锁内使用，因此本类型没有自己的同步。
/// </summary>
internal sealed class PlcPacketExchange
{
    private readonly IClientChannel channel;
    private readonly PlcPipelineSettings settings;
    private readonly ILogger logger;

    /// <summary>
    /// 初始化包交换。
    /// </summary>
    /// <param name="channel">底层通道。</param>
    /// <param name="settings">重试设置。</param>
    /// <param name="logger">日志记录器。</param>
    public PlcPacketExchange(IClientChannel channel, PlcPipelineSettings settings, ILogger logger)
    {
        this.channel = channel;
        this.settings = settings;
        this.logger = logger;
    }

    /// <summary>为第 <c>offset</c> 点起的 <c>chunk</c> 个点执行一包。</summary>
    /// <param name="offset">本包的起始点（相对于操作的起点）。</param>
    /// <param name="chunk">本包的点数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本包的结果。</returns>
    public delegate Task<CommResult> PacketAction(int offset, int chunk, CancellationToken cancellationToken);

    /// <summary>
    /// 把 <paramref name="points"/> 个点按 <paramref name="maxPoints"/> 拆成多包依次执行。任一包失败即停止，并返回该失败（不返回部分数据）。
    /// 每次调用记录一条 Debug 日志（地址、点数、包数、结果）。
    /// </summary>
    /// <param name="operation">操作名称，用于日志。</param>
    /// <param name="address">地址文本，用于日志。</param>
    /// <param name="points">总点数。</param>
    /// <param name="maxPoints">单包最大点数。</param>
    /// <param name="packet">执行单包的回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>整体结果。</returns>
    public async Task<CommResult> RunPacketsAsync(string operation, string address, int points, int maxPoints, PacketAction packet,
                                                  CancellationToken cancellationToken)
    {
        CommResult result = CommResult.Success();
        int packets = 0;
        int offset = 0;
        while (offset < points)
        {
            int chunk = Math.Min(maxPoints, points - offset);
            result = await packet(offset, chunk, cancellationToken).ConfigureAwait(false);
            packets++;
            if (!result.IsSuccess)
                break;

            offset += chunk;
        }

        LogPackets(operation, address, points, packets, result);
        return result;
    }

    /// <summary>
    /// 发送一包：构造请求、等待应答、解析应答。可重试的失败在 <see cref="PlcPipelineSettings.RetryCount"/> 之内重试，
    /// 且链路在 <see cref="PlcPipelineSettings.RetryInterval"/> 内恢复（P2-D7）；否则返回真实的失败。
    /// </summary>
    /// <param name="buildRequest">写入请求字节。</param>
    /// <param name="parseResponse">解析应答字节；应答数据由调用方在解析时收集。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本包的结果。</returns>
    public async Task<CommResult> ExchangeAsync(Action<IBufferWriter<byte>> buildRequest, Func<byte[], CommResult> parseResponse,
                                                CancellationToken cancellationToken)
    {
        for (int retry = 0; ; retry++)
        {
            CommResult result = await SendOnceAsync(buildRequest, parseResponse, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess || retry >= settings.RetryCount || !IsRetryable(result.ErrorKind))
                return result;

            logger.LogWarning("Retrying the request ({Retry} of {RetryCount}) after {ErrorKind}: {Message}",
                              retry + 1, settings.RetryCount, result.ErrorKind, result.ErrorMessage);
            if (!await channel.WaitForConnectedAsync(settings.RetryInterval, cancellationToken).ConfigureAwait(false))
                return result;
        }
    }

    private async Task<CommResult> SendOnceAsync(Action<IBufferWriter<byte>> buildRequest, Func<byte[], CommResult> parseResponse,
                                                 CancellationToken cancellationToken)
    {
        using var request = new ByteBufferWriter();
        buildRequest(request);

        CommResult<byte[]> reply = await channel.RequestAsync(request.WrittenMemory, cancellationToken: cancellationToken).ConfigureAwait(false);
        return reply.IsSuccess ? parseResponse(reply.Data) : reply.ToResult();
    }

    // 只有链路层的临时性失败才重试；对端的错误码、参数错误与取消都是终态。
    private static bool IsRetryable(CommErrorKind kind)
        => kind is CommErrorKind.Timeout or CommErrorKind.ConnectionClosed or CommErrorKind.ProtocolViolation;

    private void LogPackets(string operation, string address, int points, int packets, CommResult result)
    {
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("{Operation} {Address}: {Points} point(s) in {Packets} packet(s), {Result}",
                            operation, address, points, packets, result.ToString());
    }
}
