using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;

namespace Junevy.Communication.DebugAssistant.Protocols.Plc;

/// <summary>
/// 面板的类型化读写（计划第 7.1 节）：把 <see cref="PlcValueType"/> 映射到 <see cref="IPlcClient"/> 的类型化方法。
/// 读取返回非泛型的 <see cref="CommResult"/> 与值（失败时值为 null），这样所有操作都可以用同一种方式记录。
/// </summary>
internal static class PlcTypedAccess
{
    /// <summary>读取一个区域。<paramref name="count"/> 的含义见 <see cref="PlcValueType"/>；标量类型读取的是长度为 1 的数组。</summary>
    public static async Task<(CommResult Result, object? Value)> ReadAsync(
        IPlcClient client, PlcValueType type, string address, int count, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case PlcValueType.Int16:
                return Pack(await client.ReadInt16Async(address, count, cancellationToken));
            case PlcValueType.UInt16:
                return Pack(await client.ReadUInt16Async(address, count, cancellationToken));
            case PlcValueType.Int32:
                return Pack(await client.ReadInt32Async(address, count, cancellationToken));
            case PlcValueType.UInt32:
                return Pack(await client.ReadUInt32Async(address, count, cancellationToken));
            case PlcValueType.Int64:
                return Pack(await client.ReadInt64Async(address, count, cancellationToken));
            case PlcValueType.UInt64:
                return Pack(await client.ReadUInt64Async(address, count, cancellationToken));
            case PlcValueType.Float:
                return Pack(await client.ReadFloatAsync(address, count, cancellationToken));
            case PlcValueType.Double:
                return Pack(await client.ReadDoubleAsync(address, count, cancellationToken));
            case PlcValueType.String:
                return Pack(await client.ReadStringAsync(address, count, cancellationToken));
            case PlcValueType.Bool:
                return Pack(await client.ReadBoolAsync(address, cancellationToken));
            case PlcValueType.Bits:
                return Pack(await client.ReadBitsAsync(address, count, cancellationToken));
            case PlcValueType.Byte:
                return Pack(await client.ReadByteAsync(address, cancellationToken));
            case PlcValueType.Bytes:
                return Pack(await client.ReadBytesAsync(address, count, cancellationToken));
            case PlcValueType.Words:
                return Pack(await client.ReadAsync(address, count, cancellationToken));
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    /// <summary>
    /// 写入一个值（由 <see cref="PlcValueCodec.Parse"/> 按同一类型解析得到）。字符串在 <paramref name="stringByteLength"/> 大于 0 时按定长写入。
    /// </summary>
    public static Task<CommResult> WriteAsync(
        IPlcClient client, PlcValueType type, string address, object value, int stringByteLength, CancellationToken cancellationToken) => type switch
    {
        PlcValueType.Int16 => client.WriteInt16Async(address, (short[])value, cancellationToken),
        PlcValueType.UInt16 => client.WriteUInt16Async(address, (ushort[])value, cancellationToken),
        PlcValueType.Int32 => client.WriteInt32Async(address, (int[])value, cancellationToken),
        PlcValueType.UInt32 => client.WriteUInt32Async(address, (uint[])value, cancellationToken),
        PlcValueType.Int64 => client.WriteInt64Async(address, (long[])value, cancellationToken),
        PlcValueType.UInt64 => client.WriteUInt64Async(address, (ulong[])value, cancellationToken),
        PlcValueType.Float => client.WriteFloatAsync(address, (float[])value, cancellationToken),
        PlcValueType.Double => client.WriteDoubleAsync(address, (double[])value, cancellationToken),
        PlcValueType.String => stringByteLength > 0
            ? client.WriteStringAsync(address, (string)value, stringByteLength, cancellationToken)
            : client.WriteStringAsync(address, (string)value, cancellationToken),
        PlcValueType.Bool => client.WriteBoolAsync(address, (bool)value, cancellationToken),
        PlcValueType.Bits => client.WriteBitsAsync(address, (bool[])value, cancellationToken),
        PlcValueType.Byte => client.WriteByteAsync(address, (byte)value, cancellationToken),
        PlcValueType.Bytes => client.WriteBytesAsync(address, (byte[])value, cancellationToken),
        PlcValueType.Words => client.WriteAsync(address, (byte[])value, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>成功时保留值，失败时保留库的错误种类、消息与协议错误码（操作记录需要）。</summary>
    private static (CommResult Result, object? Value) Pack<T>(CommResult<T> result) => result.IsSuccess
        ? (CommResult.Success(), result.Data)
        : (CommResult.Fail(result.ErrorMessage, result.ErrorKind, result.ProtocolErrorCode), null);
}
