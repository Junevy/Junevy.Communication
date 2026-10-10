using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 测试协议的客户端：用 <see cref="FakeFrame"/> 实现全部协议钩子。派生的测试类可以覆写钩子来制造异常或限制。
/// </summary>
internal class FakePlcClient : PlcClientBase<TestAddress>
{
    /// <summary>默认的单包最大字数（小于 16，便于构造拆包用例）。</summary>
    public const int DefaultMaxWords = 4;

    /// <summary>默认的单包最大点数。</summary>
    public const int DefaultMaxBits = 16;

    private static readonly TestDevice[] Devices =
    {
        new TestDevice("D", PlcAddressUnit.Word, 10, 9999),
        new TestDevice("M", PlcAddressUnit.Bit, 10, 9999),
    };

    private static readonly PlcAddressParser<TestDevice, TestAddress> Parser =
        new PlcAddressParser<TestDevice, TestAddress>(Devices, (device, number, bit, text) => new TestAddress(device, number, bit, text));

    private readonly int maxWords;
    private readonly int maxBits;

    /// <summary>
    /// 创建测试客户端。选项、数据格式与日志器为 null 时使用默认值（默认选项、DCBA 字序、测试日志器）。
    /// </summary>
    /// <param name="channel">底层通道（测试替身）。</param>
    /// <param name="options">选项；为 null 时使用默认值。</param>
    /// <param name="dataFormat">数据格式；为 null 时使用 DCBA 字序。</param>
    /// <param name="logger">日志记录器；为 null 时使用新的测试日志器。</param>
    /// <param name="maxWords">单包最大字数。</param>
    /// <param name="maxBits">单包最大点数。</param>
    public FakePlcClient(IClientChannel channel, PlcClientOptions? options = null, PlcDataFormat? dataFormat = null,
                         ILogger? logger = null, int maxWords = DefaultMaxWords, int maxBits = DefaultMaxBits)
        : this(maxWords, maxBits, channel, options ?? new PlcClientOptions(), dataFormat ?? new PlcDataFormat(WordOrder.DCBA),
               logger ?? new TestLogger())
    {
    }

    /// <summary>
    /// 按原样传递参数的构造函数（不替换 null），用于测试基类的参数校验。参数顺序与公开构造函数不同，以避免签名冲突。
    /// </summary>
    protected FakePlcClient(int maxWords, int maxBits, IClientChannel channel, PlcClientOptions options, PlcDataFormat dataFormat, ILogger logger)
        : base(channel, options, dataFormat, logger)
    {
        this.maxWords = maxWords;
        this.maxBits = maxBits;
    }

    /// <summary>设备的符号（单字符，例如 'D'）。</summary>
    protected static char SymbolOf(TestAddress address) => address.Device.Symbol[0];

    /// <inheritdoc />
    protected internal override bool TryParseAddress(string address, [NotNullWhen(true)] out TestAddress? parsed, [NotNullWhen(false)] out string? error)
        => Parser.TryParse(address, out parsed, out error);

    /// <inheritdoc />
    protected internal override int GetMaxReadPoints(TestAddress address, PlcAccess access) => access == PlcAccess.Words ? maxWords : maxBits;

    /// <inheritdoc />
    protected internal override int GetMaxWritePoints(TestAddress address, PlcAccess access) => access == PlcAccess.Words ? maxWords : maxBits;

    /// <inheritdoc />
    protected internal override void BuildReadWordsRequest(TestAddress address, int offset, int count, IBufferWriter<byte> output)
        => FakeFrame.WriteHeader(output, FakeFrame.ReadWordsOp, SymbolOf(address), address.Number + offset, count);

    /// <inheritdoc />
    protected internal override CommResult<byte[]> ParseReadWordsResponse(int count, ReadOnlySpan<byte> frame)
        => FakeFrame.ParseData(frame, count * FakeFrame.WordBytes);

    /// <inheritdoc />
    protected internal override void BuildWriteWordsRequest(TestAddress address, int offset, ReadOnlySpan<byte> words, IBufferWriter<byte> output)
    {
        FakeFrame.WriteHeader(output, FakeFrame.WriteWordsOp, SymbolOf(address), address.Number + offset, words.Length / FakeFrame.WordBytes);
        FakeFrame.WriteBytes(output, words);
    }

    /// <inheritdoc />
    protected internal override void BuildReadBitsRequest(TestAddress address, int offset, int count, IBufferWriter<byte> output)
        => FakeFrame.WriteHeader(output, FakeFrame.ReadBitsOp, SymbolOf(address), address.Number + offset, count);

    /// <inheritdoc />
    protected internal override CommResult<bool[]> ParseReadBitsResponse(int count, ReadOnlySpan<byte> frame)
    {
        CommResult<byte[]> data = FakeFrame.ParseData(frame, count);
        if (!data.IsSuccess)
            return data.As<bool[]>();

        return CommResult<bool[]>.Success(Array.ConvertAll(data.Data!, value => value != 0));
    }

    /// <inheritdoc />
    protected internal override void BuildWriteBitsRequest(TestAddress address, int offset, ReadOnlySpan<bool> values, IBufferWriter<byte> output)
    {
        FakeFrame.WriteHeader(output, FakeFrame.WriteBitsOp, SymbolOf(address), address.Number + offset, values.Length);
        byte[] payload = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
            payload[i] = (byte)(values[i] ? 1 : 0);

        FakeFrame.WriteBytes(output, payload);
    }

    /// <inheritdoc />
    protected internal override CommResult ParseWriteResponse(ReadOnlySpan<byte> frame) => FakeFrame.ParseAck(frame);
}
