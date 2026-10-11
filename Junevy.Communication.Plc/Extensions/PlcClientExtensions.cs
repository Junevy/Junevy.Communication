using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc.Pipeline;

namespace Junevy.Communication.Plc;

/// <summary>
/// 类型化读写扩展：把 <see cref="IPlcClient"/> 的字节读写转换为数值、字符串与位。
/// 16 位占 1 个字，32 位占 2 个字，64 位占 4 个字；数值按 <see cref="PlcDataFormat.WordOrder"/> 转换（<see cref="ByteTransform"/>）。
/// 底层失败原样透传（错误分类与协议码不变）。
/// </summary>
/// <remarks>
/// 参数为 null 时抛出 <see cref="ArgumentNullException"/>；数量为零或数据为空时返回 <c>InvalidRequest</c>。
/// 字符串与位的扩展位于同一个类的另一部分文件中；字符串按 <see cref="PlcDataFormat.StringEncoding"/> 编码，
/// 并按软元件单位决定是否必须整字（P4-D7）。
/// </remarks>
public static partial class PlcClientExtensions
{
    private const int Int16Words = sizeof(short) / PlcLayout.WordBytes;
    private const int UInt16Words = sizeof(ushort) / PlcLayout.WordBytes;
    private const int Int32Words = sizeof(int) / PlcLayout.WordBytes;
    private const int UInt32Words = sizeof(uint) / PlcLayout.WordBytes;
    private const int Int64Words = sizeof(long) / PlcLayout.WordBytes;
    private const int UInt64Words = sizeof(ulong) / PlcLayout.WordBytes;
    private const int SingleWords = sizeof(float) / PlcLayout.WordBytes;
    private const int DoubleWords = sizeof(double) / PlcLayout.WordBytes;

    // ————— 数值：8 种类型共用下方的一个读辅助与一个写辅助；公开方法只是一行转发 —————

    /// <summary>读取一个 16 位有符号整数（1 个字）。</summary>
    public static async Task<CommResult<short>> ReadInt16Async(this IPlcClient client, string address, CancellationToken cancellationToken = default)
        => First(await ReadValuesAsync(client, address, 1, Int16Words, ByteTransform.ToInt16, cancellationToken).ConfigureAwait(false));

    /// <summary>读取多个 16 位有符号整数（每个 1 个字）。</summary>
    public static Task<CommResult<short[]>> ReadInt16Async(this IPlcClient client, string address, int count, CancellationToken cancellationToken = default)
        => ReadValuesAsync(client, address, count, Int16Words, ByteTransform.ToInt16, cancellationToken);

    /// <summary>写入一个 16 位有符号整数（1 个字）。</summary>
    public static Task<CommResult> WriteInt16Async(this IPlcClient client, string address, short value, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, Int16Words, new[] { value }, ByteTransform.WriteInt16, cancellationToken);

    /// <summary>写入多个 16 位有符号整数（每个 1 个字）。</summary>
    public static Task<CommResult> WriteInt16Async(this IPlcClient client, string address, short[] values, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, Int16Words, values, ByteTransform.WriteInt16, cancellationToken);

    /// <summary>读取一个 16 位无符号整数（1 个字）。</summary>
    public static async Task<CommResult<ushort>> ReadUInt16Async(this IPlcClient client, string address, CancellationToken cancellationToken = default)
        => First(await ReadValuesAsync(client, address, 1, UInt16Words, ByteTransform.ToUInt16, cancellationToken).ConfigureAwait(false));

    /// <summary>读取多个 16 位无符号整数（每个 1 个字）。</summary>
    public static Task<CommResult<ushort[]>> ReadUInt16Async(this IPlcClient client, string address, int count, CancellationToken cancellationToken = default)
        => ReadValuesAsync(client, address, count, UInt16Words, ByteTransform.ToUInt16, cancellationToken);

    /// <summary>写入一个 16 位无符号整数（1 个字）。</summary>
    public static Task<CommResult> WriteUInt16Async(this IPlcClient client, string address, ushort value, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, UInt16Words, new[] { value }, ByteTransform.WriteUInt16, cancellationToken);

    /// <summary>写入多个 16 位无符号整数（每个 1 个字）。</summary>
    public static Task<CommResult> WriteUInt16Async(this IPlcClient client, string address, ushort[] values, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, UInt16Words, values, ByteTransform.WriteUInt16, cancellationToken);

    /// <summary>读取一个 32 位有符号整数（2 个字）。</summary>
    public static async Task<CommResult<int>> ReadInt32Async(this IPlcClient client, string address, CancellationToken cancellationToken = default)
        => First(await ReadValuesAsync(client, address, 1, Int32Words, ByteTransform.ToInt32, cancellationToken).ConfigureAwait(false));

    /// <summary>读取多个 32 位有符号整数（每个 2 个字）。</summary>
    public static Task<CommResult<int[]>> ReadInt32Async(this IPlcClient client, string address, int count, CancellationToken cancellationToken = default)
        => ReadValuesAsync(client, address, count, Int32Words, ByteTransform.ToInt32, cancellationToken);

    /// <summary>写入一个 32 位有符号整数（2 个字）。</summary>
    public static Task<CommResult> WriteInt32Async(this IPlcClient client, string address, int value, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, Int32Words, new[] { value }, ByteTransform.WriteInt32, cancellationToken);

    /// <summary>写入多个 32 位有符号整数（每个 2 个字）。</summary>
    public static Task<CommResult> WriteInt32Async(this IPlcClient client, string address, int[] values, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, Int32Words, values, ByteTransform.WriteInt32, cancellationToken);

    /// <summary>读取一个 32 位无符号整数（2 个字）。</summary>
    public static async Task<CommResult<uint>> ReadUInt32Async(this IPlcClient client, string address, CancellationToken cancellationToken = default)
        => First(await ReadValuesAsync(client, address, 1, UInt32Words, ByteTransform.ToUInt32, cancellationToken).ConfigureAwait(false));

    /// <summary>读取多个 32 位无符号整数（每个 2 个字）。</summary>
    public static Task<CommResult<uint[]>> ReadUInt32Async(this IPlcClient client, string address, int count, CancellationToken cancellationToken = default)
        => ReadValuesAsync(client, address, count, UInt32Words, ByteTransform.ToUInt32, cancellationToken);

    /// <summary>写入一个 32 位无符号整数（2 个字）。</summary>
    public static Task<CommResult> WriteUInt32Async(this IPlcClient client, string address, uint value, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, UInt32Words, new[] { value }, ByteTransform.WriteUInt32, cancellationToken);

    /// <summary>写入多个 32 位无符号整数（每个 2 个字）。</summary>
    public static Task<CommResult> WriteUInt32Async(this IPlcClient client, string address, uint[] values, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, UInt32Words, values, ByteTransform.WriteUInt32, cancellationToken);

    /// <summary>读取一个 64 位有符号整数（4 个字）。</summary>
    public static async Task<CommResult<long>> ReadInt64Async(this IPlcClient client, string address, CancellationToken cancellationToken = default)
        => First(await ReadValuesAsync(client, address, 1, Int64Words, ByteTransform.ToInt64, cancellationToken).ConfigureAwait(false));

    /// <summary>读取多个 64 位有符号整数（每个 4 个字）。</summary>
    public static Task<CommResult<long[]>> ReadInt64Async(this IPlcClient client, string address, int count, CancellationToken cancellationToken = default)
        => ReadValuesAsync(client, address, count, Int64Words, ByteTransform.ToInt64, cancellationToken);

    /// <summary>写入一个 64 位有符号整数（4 个字）。</summary>
    public static Task<CommResult> WriteInt64Async(this IPlcClient client, string address, long value, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, Int64Words, new[] { value }, ByteTransform.WriteInt64, cancellationToken);

    /// <summary>写入多个 64 位有符号整数（每个 4 个字）。</summary>
    public static Task<CommResult> WriteInt64Async(this IPlcClient client, string address, long[] values, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, Int64Words, values, ByteTransform.WriteInt64, cancellationToken);

    /// <summary>读取一个 64 位无符号整数（4 个字）。</summary>
    public static async Task<CommResult<ulong>> ReadUInt64Async(this IPlcClient client, string address, CancellationToken cancellationToken = default)
        => First(await ReadValuesAsync(client, address, 1, UInt64Words, ByteTransform.ToUInt64, cancellationToken).ConfigureAwait(false));

    /// <summary>读取多个 64 位无符号整数（每个 4 个字）。</summary>
    public static Task<CommResult<ulong[]>> ReadUInt64Async(this IPlcClient client, string address, int count, CancellationToken cancellationToken = default)
        => ReadValuesAsync(client, address, count, UInt64Words, ByteTransform.ToUInt64, cancellationToken);

    /// <summary>写入一个 64 位无符号整数（4 个字）。</summary>
    public static Task<CommResult> WriteUInt64Async(this IPlcClient client, string address, ulong value, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, UInt64Words, new[] { value }, ByteTransform.WriteUInt64, cancellationToken);

    /// <summary>写入多个 64 位无符号整数（每个 4 个字）。</summary>
    public static Task<CommResult> WriteUInt64Async(this IPlcClient client, string address, ulong[] values, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, UInt64Words, values, ByteTransform.WriteUInt64, cancellationToken);

    /// <summary>读取一个单精度浮点数（2 个字）。</summary>
    public static async Task<CommResult<float>> ReadFloatAsync(this IPlcClient client, string address, CancellationToken cancellationToken = default)
        => First(await ReadValuesAsync(client, address, 1, SingleWords, ByteTransform.ToSingle, cancellationToken).ConfigureAwait(false));

    /// <summary>读取多个单精度浮点数（每个 2 个字）。</summary>
    public static Task<CommResult<float[]>> ReadFloatAsync(this IPlcClient client, string address, int count, CancellationToken cancellationToken = default)
        => ReadValuesAsync(client, address, count, SingleWords, ByteTransform.ToSingle, cancellationToken);

    /// <summary>写入一个单精度浮点数（2 个字）。</summary>
    public static Task<CommResult> WriteFloatAsync(this IPlcClient client, string address, float value, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, SingleWords, new[] { value }, ByteTransform.WriteSingle, cancellationToken);

    /// <summary>写入多个单精度浮点数（每个 2 个字）。</summary>
    public static Task<CommResult> WriteFloatAsync(this IPlcClient client, string address, float[] values, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, SingleWords, values, ByteTransform.WriteSingle, cancellationToken);

    /// <summary>读取一个双精度浮点数（4 个字）。</summary>
    public static async Task<CommResult<double>> ReadDoubleAsync(this IPlcClient client, string address, CancellationToken cancellationToken = default)
        => First(await ReadValuesAsync(client, address, 1, DoubleWords, ByteTransform.ToDouble, cancellationToken).ConfigureAwait(false));

    /// <summary>读取多个双精度浮点数（每个 4 个字）。</summary>
    public static Task<CommResult<double[]>> ReadDoubleAsync(this IPlcClient client, string address, int count, CancellationToken cancellationToken = default)
        => ReadValuesAsync(client, address, count, DoubleWords, ByteTransform.ToDouble, cancellationToken);

    /// <summary>写入一个双精度浮点数（4 个字）。</summary>
    public static Task<CommResult> WriteDoubleAsync(this IPlcClient client, string address, double value, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, DoubleWords, new[] { value }, ByteTransform.WriteDouble, cancellationToken);

    /// <summary>写入多个双精度浮点数（每个 4 个字）。</summary>
    public static Task<CommResult> WriteDoubleAsync(this IPlcClient client, string address, double[] values, CancellationToken cancellationToken = default)
        => WriteValuesAsync(client, address, DoubleWords, values, ByteTransform.WriteDouble, cancellationToken);

    private delegate T SpanDecoder<T>(ReadOnlySpan<byte> source, WordOrder order);

    private delegate void SpanEncoder<T>(Span<byte> destination, T value, WordOrder order);

    // 读取 count 个值，每个占 words 个字；结果按地址顺序排列。
    private static async Task<CommResult<T[]>> ReadValuesAsync<T>(IPlcClient client, string address, int count, int words, SpanDecoder<T> decode,
                                                                 CancellationToken cancellationToken)
    {
        RequireClient(client);
        CommResult? tooLarge = CheckSize(count, words);
        if (tooLarge is not null)
            return tooLarge.As<T[]>();

        CommResult<byte[]> bytes = await client.ReadAsync(address, count * words, cancellationToken).ConfigureAwait(false);
        if (!bytes.IsSuccess)
            return bytes.As<T[]>();

        int bytesPerValue = words * PlcLayout.WordBytes;
        var values = new T[count];
        for (int i = 0; i < count; i++)
            values[i] = decode(bytes.Data!.AsSpan(i * bytesPerValue), client.DataFormat.WordOrder);

        return CommResult<T[]>.Success(values);
    }

    // 写入 values，每个值占 words 个字；空数组由 WriteAsync 返回 InvalidRequest。
    private static async Task<CommResult> WriteValuesAsync<T>(IPlcClient client, string address, int words, T[] values, SpanEncoder<T> encode,
                                                             CancellationToken cancellationToken)
    {
        RequireClient(client);
        if (values is null)
            throw new ArgumentNullException(nameof(values));

        CommResult? tooLarge = CheckSize(values.Length, words);
        if (tooLarge is not null)
            return tooLarge;

        int bytesPerValue = words * PlcLayout.WordBytes;
        byte[] data = new byte[values.Length * bytesPerValue];
        for (int i = 0; i < values.Length; i++)
            encode(data.AsSpan(i * bytesPerValue, bytesPerValue), values[i], client.DataFormat.WordOrder);

        return await client.WriteAsync(address, data, cancellationToken).ConfigureAwait(false);
    }

    private static CommResult<T> First<T>(CommResult<T[]> values)
        => values.IsSuccess ? CommResult<T>.Success(values.Data![0]) : values.As<T>();

    // 字数超过上限时返回 InvalidRequest。使用 long 计算，避免溢出；数量小于 1 由 ReadAsync / WriteAsync 处理。
    private static CommResult? CheckSize(int count, int words)
        => (long)count * words > PlcLayout.MaxWordCount
            ? CommResult.Fail("The count is too large.", CommErrorKind.InvalidRequest)
            : null;

    private static void RequireClient(IPlcClient client)
    {
        if (client is null)
            throw new ArgumentNullException(nameof(client));
    }
}
