using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 设备级传输：把一次字或位访问拆成多包，并把协议的请求与应答钩子接到包交换上。
/// 本类型不校验地址与数量（由流水线在入口完成），只负责拆包与收集结果。
/// </summary>
internal sealed class PlcTransfer<TAddress> where TAddress : PlcAddress
{
    private readonly PlcClientBase<TAddress> client;
    private readonly PlcPacketExchange exchange;

    /// <summary>
    /// 初始化设备级传输。
    /// </summary>
    /// <param name="client">协议客户端（协议钩子的所有者）。</param>
    /// <param name="exchange">包交换。</param>
    public PlcTransfer(PlcClientBase<TAddress> client, PlcPacketExchange exchange)
    {
        this.client = client;
        this.exchange = exchange;
    }

    /// <summary>
    /// 按字访问读取 <paramref name="count"/> 个字。
    /// </summary>
    /// <param name="operation">操作名称，用于日志。</param>
    /// <param name="target">已解析的地址。</param>
    /// <param name="count">字数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>规范化布局的字节（每字 2 字节）。</returns>
    public async Task<CommResult<byte[]>> ReadWordsAsync(string operation, TAddress target, int count, CancellationToken cancellationToken)
    {
        int maxPoints = PlcLayout.RequirePositive(client.GetMaxReadPoints(target, PlcAccess.Words), "GetMaxReadPoints");
        byte[] words = new byte[count * PlcLayout.WordBytes];
        CommResult result = await exchange.RunPacketsAsync(operation, target.Text, count, maxPoints,
            (offset, chunk, token) => exchange.ExchangeAsync(
                request => client.BuildReadWordsRequest(target, offset, chunk, request),
                frame => CollectWords(client.ParseReadWordsResponse(chunk, frame), words, offset, chunk),
                token),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? CommResult<byte[]>.Success(words) : result.As<byte[]>();
    }

    /// <summary>
    /// 按字访问写入字数据。
    /// </summary>
    /// <param name="operation">操作名称，用于日志。</param>
    /// <param name="target">已解析的地址。</param>
    /// <param name="words">规范化布局的字节，长度为偶数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果。</returns>
    public Task<CommResult> WriteWordsAsync(string operation, TAddress target, ReadOnlyMemory<byte> words, CancellationToken cancellationToken)
    {
        int maxPoints = PlcLayout.RequirePositive(client.GetMaxWritePoints(target, PlcAccess.Words), "GetMaxWritePoints");
        return exchange.RunPacketsAsync(operation, target.Text, words.Length / PlcLayout.WordBytes, maxPoints,
            (offset, chunk, token) => exchange.ExchangeAsync(
                request => client.BuildWriteWordsRequest(target, offset, words.Span.Slice(offset * PlcLayout.WordBytes, chunk * PlcLayout.WordBytes), request),
                frame => client.ParseWriteResponse(frame),
                token),
            cancellationToken);
    }

    /// <summary>
    /// 按位访问读取 <paramref name="count"/> 个点（位软元件）。
    /// </summary>
    /// <param name="operation">操作名称，用于日志。</param>
    /// <param name="target">已解析的位软元件地址。</param>
    /// <param name="count">点数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>每点的值。</returns>
    public async Task<CommResult<bool[]>> ReadBitsAsync(string operation, TAddress target, int count, CancellationToken cancellationToken)
    {
        int maxPoints = PlcLayout.RequirePositive(client.GetMaxReadPoints(target, PlcAccess.Bits), "GetMaxReadPoints");
        bool[] bits = new bool[count];
        CommResult result = await exchange.RunPacketsAsync(operation, target.Text, count, maxPoints,
            (offset, chunk, token) => exchange.ExchangeAsync(
                request => client.BuildReadBitsRequest(target, offset, chunk, request),
                frame => CollectBits(client.ParseReadBitsResponse(chunk, frame), bits, offset, chunk),
                token),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? CommResult<bool[]>.Success(bits) : result.As<bool[]>();
    }

    /// <summary>
    /// 按位访问写入位（位软元件）。
    /// </summary>
    /// <param name="operation">操作名称，用于日志。</param>
    /// <param name="target">已解析的位软元件地址。</param>
    /// <param name="values">每点的值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果。</returns>
    public Task<CommResult> WriteBitsAsync(string operation, TAddress target, ReadOnlyMemory<bool> values, CancellationToken cancellationToken)
    {
        int maxPoints = PlcLayout.RequirePositive(client.GetMaxWritePoints(target, PlcAccess.Bits), "GetMaxWritePoints");
        return exchange.RunPacketsAsync(operation, target.Text, values.Length, maxPoints,
            (offset, chunk, token) => exchange.ExchangeAsync(
                request => client.BuildWriteBitsRequest(target, offset, values.Span.Slice(offset, chunk), request),
                frame => client.ParseWriteResponse(frame),
                token),
            cancellationToken);
    }

    // 把一包的应答数据复制到结果缓冲区的对应位置；数据长度不符是协议实现错误。
    private static CommResult CollectWords(CommResult<byte[]> packet, byte[] destination, int offset, int chunk)
    {
        if (!packet.IsSuccess)
            return packet.ToResult();

        int length = chunk * PlcLayout.WordBytes;
        PlcLayout.RequireLength(packet.Data!.Length, length, "ParseReadWordsResponse");
        Array.Copy(packet.Data, 0, destination, offset * PlcLayout.WordBytes, length);
        return CommResult.Success();
    }

    private static CommResult CollectBits(CommResult<bool[]> packet, bool[] destination, int offset, int chunk)
    {
        if (!packet.IsSuccess)
            return packet.ToResult();

        PlcLayout.RequireLength(packet.Data!.Length, chunk, "ParseReadBitsResponse");
        Array.Copy(packet.Data, 0, destination, offset, chunk);
        return CommResult.Success();
    }
}
