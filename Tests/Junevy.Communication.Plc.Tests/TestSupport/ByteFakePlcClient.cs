using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 实现字节钩子的测试协议：继承 <see cref="FakePlcClient"/> 的字与位实现，只补齐按字节的访问与按字节的单包上限。
/// 字节软元件（B）的应答按设备内存顺序返回，不做字序转换。
/// </summary>
internal class ByteFakePlcClient : FakePlcClient
{
    /// <summary>默认的单包最大字节数（小于 8，便于构造跨字节的拆包用例）。</summary>
    public const int DefaultMaxBytes = 6;

    private readonly int maxBytes;

    /// <summary>
    /// 创建测试客户端。
    /// </summary>
    /// <param name="channel">底层通道（测试替身）。</param>
    /// <param name="options">选项；为 null 时使用默认值。</param>
    /// <param name="logger">日志记录器；为 null 时使用新的测试日志器。</param>
    /// <param name="maxBytes">单包最大字节数。</param>
    public ByteFakePlcClient(IClientChannel channel, PlcClientOptions? options = null, ILogger? logger = null,
                             int maxBytes = DefaultMaxBytes)
        : base(channel, options, dataFormat: null, logger)
    {
        this.maxBytes = maxBytes;
    }

    /// <inheritdoc />
    protected internal override int GetMaxReadPoints(TestAddress address, PlcAccess access)
        => access == PlcAccess.Bytes ? maxBytes : base.GetMaxReadPoints(address, access);

    /// <inheritdoc />
    protected internal override int GetMaxWritePoints(TestAddress address, PlcAccess access)
        => access == PlcAccess.Bytes ? maxBytes : base.GetMaxWritePoints(address, access);

    /// <inheritdoc />
    protected internal override void BuildReadBytesRequest(TestAddress address, int offset, int count, IBufferWriter<byte> output)
        => FakeFrame.WriteHeader(output, FakeFrame.ReadBytesOp, SymbolOf(address), address.Number + offset, count);

    /// <inheritdoc />
    protected internal override CommResult<byte[]> ParseReadBytesResponse(int count, ReadOnlySpan<byte> frame)
        => FakeFrame.ParseData(frame, count);

    /// <inheritdoc />
    protected internal override void BuildWriteBytesRequest(TestAddress address, int offset, ReadOnlySpan<byte> bytes, IBufferWriter<byte> output)
    {
        FakeFrame.WriteHeader(output, FakeFrame.WriteBytesOp, SymbolOf(address), address.Number + offset, bytes.Length);
        FakeFrame.WriteBytes(output, bytes);
    }
}
