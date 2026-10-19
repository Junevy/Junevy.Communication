using System.Buffers.Binary;
using Junevy.Communication.Channels;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels;

/// <summary>字节序（计划 DA-D12 的键字段）。</summary>
public enum KeyByteOrder
{
    BigEndian,
    LittleEndian,
}

/// <summary>键字段：偏移、宽度（1、2 或 4 字节）与字节序。</summary>
public sealed record KeyField(int Offset, int Width, KeyByteOrder Order)
{
    /// <summary>检查参数：偏移不能为负，宽度只能是 1、2、4。</summary>
    public KeyField Validated()
    {
        if (Offset < 0)
        {
            throw new ArgumentException("The key offset must not be negative.", nameof(Offset));
        }

        if (Width is not (1 or 2 or 4))
        {
            throw new ArgumentException("The key width must be 1, 2 or 4 bytes.", nameof(Width));
        }

        return this;
    }
}

/// <summary>
/// 按界面配置的键提取器（计划 DA-D12）：请求与应答各自的键字段。长度不足时返回失败，从不抛出异常。
/// </summary>
internal sealed class ConfigurableKeyExtractor : IFrameKeyExtractor
{
    private readonly KeyField _request;
    private readonly KeyField _response;

    public ConfigurableKeyExtractor(KeyField request, KeyField response)
    {
        _request = request.Validated();
        _response = response.Validated();
    }

    public bool TryGetRequestKey(ReadOnlySpan<byte> request, out long key) => TryRead(_request, request, out key);

    public bool TryGetResponseKey(ReadOnlySpan<byte> frame, out long key) => TryRead(_response, frame, out key);

    private static bool TryRead(KeyField field, ReadOnlySpan<byte> data, out long key)
    {
        key = 0;
        if ((long)field.Offset + field.Width > data.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> slice = data.Slice(field.Offset, field.Width);
        bool bigEndian = field.Order == KeyByteOrder.BigEndian;
        key = field.Width switch
        {
            1 => slice[0],
            2 => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(slice) : BinaryPrimitives.ReadUInt16LittleEndian(slice),
            _ => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(slice) : BinaryPrimitives.ReadUInt32LittleEndian(slice),
        };
        return true;
    }
}
