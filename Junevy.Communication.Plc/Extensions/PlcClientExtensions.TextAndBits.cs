using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc.Pipeline;

namespace Junevy.Communication.Plc;

public static partial class PlcClientExtensions
{
    /// <summary>
    /// 读取字符串：占 ceil(<paramref name="byteLength"/> / 2) 个字，用字符串编码解码前 <paramref name="byteLength"/> 个字节，并在第一个 '\0' 处截断。
    /// </summary>
    /// <param name="client">客户端。</param>
    /// <param name="address">地址，例如 "D100"。</param>
    /// <param name="byteLength">字节数，必须 ≥ 1。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>字符串；字节数不合法时返回 <c>InvalidRequest</c>。</returns>
    public static async Task<CommResult<string>> ReadStringAsync(this IPlcClient client, string address, int byteLength,
                                                                  CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        if (byteLength < 1)
            return CommResult<string>.Fail("The byte length must be at least 1.", CommErrorKind.InvalidRequest);

        // 向上取整，写成两项之和以避免 byteLength 接近 int.MaxValue 时溢出。
        int words = byteLength / PlcLayout.WordBytes + byteLength % PlcLayout.WordBytes;
        CommResult<byte[]> bytes = await client.ReadAsync(address, words, cancellationToken).ConfigureAwait(false);
        if (!bytes.IsSuccess)
            return bytes.As<string>();

        string text = client.DataFormat.StringEncoding.GetString(bytes.Data!, 0, byteLength);
        int terminator = text.IndexOf('\0');
        return CommResult<string>.Success(terminator < 0 ? text : text.Substring(0, terminator));
    }

    /// <summary>
    /// 写入字符串：按字符串编码；编码后为奇数长度时补一个 0x00，使最后一个字完整。
    /// </summary>
    /// <param name="client">客户端。</param>
    /// <param name="address">地址，例如 "D100"。</param>
    /// <param name="value">要写入的字符串；为 null 或空串时返回 <c>InvalidRequest</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果。</returns>
    public static async Task<CommResult> WriteStringAsync(this IPlcClient client, string address, string value,
                                                          CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        if (string.IsNullOrEmpty(value))
            return CommResult.Fail("The string must not be empty.", CommErrorKind.InvalidRequest);

        byte[] encoded = client.DataFormat.StringEncoding.GetBytes(value);
        byte[] data = new byte[encoded.Length + encoded.Length % PlcLayout.WordBytes];
        encoded.CopyTo(data, 0);
        return await client.WriteAsync(address, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取一个位：位软元件取 1 点；字软元件（可带位号）取覆盖位的字中的对应位。</summary>
    /// <param name="client">客户端。</param>
    /// <param name="address">地址，例如 "M0" 或 "D100.5"。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>位的值。</returns>
    public static async Task<CommResult<bool>> ReadBoolAsync(this IPlcClient client, string address, CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        CommResult<bool[]> bits = await client.ReadBitsAsync(address, 1, cancellationToken).ConfigureAwait(false);
        return bits.IsSuccess ? CommResult<bool>.Success(bits.Data![0]) : bits.As<bool>();
    }

    /// <summary>写入一个位（规则同 <c>WriteBitsAsync</c>，包括字软元件的读-改-写开关）。</summary>
    /// <param name="client">客户端。</param>
    /// <param name="address">地址，例如 "M0" 或 "D100.5"。</param>
    /// <param name="value">要写入的值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果。</returns>
    public static async Task<CommResult> WriteBoolAsync(this IPlcClient client, string address, bool value, CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        return await client.WriteBitsAsync(address, new[] { value }, cancellationToken).ConfigureAwait(false);
    }
}
