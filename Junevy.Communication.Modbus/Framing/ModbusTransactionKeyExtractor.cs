using System.Buffers.Binary;
using Junevy.Communication.Channels;

namespace Junevy.Communication.Modbus.Framing;

/// <summary>
/// MBAP 的关联键（P7-D9）：请求与应答的前两个字节（大端）是事务号。超时后到达的迟到应答因为事务号不匹配而被丢弃。
/// </summary>
internal sealed class ModbusTransactionKeyExtractor : IFrameKeyExtractor
{
    private const int TransactionIdLength = 2;

    /// <inheritdoc />
    public bool TryGetRequestKey(ReadOnlySpan<byte> request, out long key) => TryReadTransactionId(request, out key);

    /// <inheritdoc />
    public bool TryGetResponseKey(ReadOnlySpan<byte> frame, out long key) => TryReadTransactionId(frame, out key);

    private static bool TryReadTransactionId(ReadOnlySpan<byte> bytes, out long key)
    {
        if (bytes.Length < TransactionIdLength)
        {
            key = 0;
            return false;
        }

        key = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        return true;
    }
}
