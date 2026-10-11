using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc.Pipeline;

namespace Junevy.Communication.Plc;

public static partial class PlcClientExtensions
{
    private const string ByteLengthMessage = "The byte length must be at least 1.";
    private const string EmptyStringMessage = "The string must not be empty.";
    private const string OddFixedLengthMessage = "A fixed string length on a word or bit device must be a positive even number.";

    /// <summary>
    /// 读取字符串：读取 <paramref name="byteLength"/> 个字节（字节软元件按字节访问，字与位软元件读覆盖的整字），
    /// 用 <see cref="PlcDataFormat.StringEncoding"/> 解码，并在第一个 '\0' 处截断。
    /// </summary>
    /// <param name="client">客户端。</param>
    /// <param name="address">地址，例如 "D100" 或 "DB1.DBB0"；不能带位号。</param>
    /// <param name="byteLength">字节数，必须 ≥ 1。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>字符串；字节数不合法或地址非法时返回 <c>InvalidRequest</c>。</returns>
    public static async Task<CommResult<string>> ReadStringAsync(this IPlcClient client, string address, int byteLength,
                                                                 CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        if (byteLength < 1)
            return CommResult<string>.Fail(ByteLengthMessage, CommErrorKind.InvalidRequest);

        CommResult<byte[]> bytes = await client.ReadBytesAsync(address, byteLength, cancellationToken).ConfigureAwait(false);
        if (!bytes.IsSuccess)
            return bytes.As<string>();

        string text = client.DataFormat.StringEncoding.GetString(bytes.Data!, 0, byteLength);
        int terminator = text.IndexOf('\0');
        return CommResult<string>.Success(terminator < 0 ? text : text.Substring(0, terminator));
    }

    /// <summary>
    /// 写入字符串：按 <see cref="PlcDataFormat.StringEncoding"/> 编码。字节软元件精确写入这些字节；字与位软元件在编码后为奇数长度时
    /// 补一个 0x00，使最后一个字完整（P4-D7）。
    /// </summary>
    /// <param name="client">客户端。</param>
    /// <param name="address">地址，例如 "D100" 或 "DB1.DBB0"；不能带位号。</param>
    /// <param name="value">要写入的字符串；为 null 或空串时返回 <c>InvalidRequest</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果。</returns>
    public static async Task<CommResult> WriteStringAsync(this IPlcClient client, string address, string value,
                                                          CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        if (string.IsNullOrEmpty(value))
            return CommResult.Fail(EmptyStringMessage, CommErrorKind.InvalidRequest);

        byte[] encoded = client.DataFormat.StringEncoding.GetBytes(value);
        CommResult<bool> wholeWords = RequiresWholeWords(client, address);
        if (!wholeWords.IsSuccess)
            return wholeWords.ToResult();

        // 字与位软元件上补满最后一个字；字节软元件按编码长度精确写入。
        byte[] data = wholeWords.Data ? Expand(encoded, encoded.Length + encoded.Length % PlcLayout.WordBytes) : encoded;
        return await client.WriteBytesAsync(address, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按固定长度写入字符串：编码后用 0x00 补满 <paramref name="byteLength"/> 个字节，用来清掉旧的长字符串留下的残留字符。
    /// 编码结果超过 <paramref name="byteLength"/> 时返回 <c>InvalidRequest</c>；字与位软元件要求 <paramref name="byteLength"/> 为正偶数
    /// （P4-D7）。
    /// </summary>
    /// <param name="client">客户端。</param>
    /// <param name="address">地址，例如 "D100" 或 "DB1.DBB0"；不能带位号。</param>
    /// <param name="value">要写入的字符串；为 null 或空串时返回 <c>InvalidRequest</c>。</param>
    /// <param name="byteLength">字段占用的字节数，必须 ≥ 1。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果。</returns>
    public static async Task<CommResult> WriteStringAsync(this IPlcClient client, string address, string value, int byteLength,
                                                          CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        if (byteLength < 1)
            return CommResult.Fail(ByteLengthMessage, CommErrorKind.InvalidRequest);
        if (string.IsNullOrEmpty(value))
            return CommResult.Fail(EmptyStringMessage, CommErrorKind.InvalidRequest);

        byte[] encoded = client.DataFormat.StringEncoding.GetBytes(value);
        if (encoded.Length > byteLength)
            return CommResult.Fail($"The string encodes to {encoded.Length} byte(s), which does not fit the fixed field length of {byteLength} byte(s).",
                                   CommErrorKind.InvalidRequest);

        CommResult<bool> wholeWords = RequiresWholeWords(client, address);
        if (!wholeWords.IsSuccess)
            return wholeWords.ToResult();
        if (wholeWords.Data && byteLength % PlcLayout.WordBytes != 0)
            return CommResult.Fail(OddFixedLengthMessage, CommErrorKind.InvalidRequest);

        return await client.WriteBytesAsync(address, Expand(encoded, byteLength), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取 1 个字节（P4-D7）。字节软元件精确读 1 个字节；字与位软元件读覆盖的整字，返回规范化布局的第一个字节——
    /// 对端的字序决定它是字的哪一半（MELSEC 的 DCBA 是低字节，FINS 的大端字是高字节）。
    /// </summary>
    /// <param name="client">客户端。</param>
    /// <param name="address">地址，例如 "D100" 或 "DB1.DBB0"；不能带位号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>字节的值；地址非法时返回 <c>InvalidRequest</c>。</returns>
    public static async Task<CommResult<byte>> ReadByteAsync(this IPlcClient client, string address, CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        CommResult<byte[]> bytes = await client.ReadBytesAsync(address, 1, cancellationToken).ConfigureAwait(false);
        return bytes.IsSuccess ? CommResult<byte>.Success(bytes.Data![0]) : bytes.As<byte>();
    }

    /// <summary>
    /// 写入 1 个字节（P4-D7）。字节软元件精确写入；字与位软元件只能整字写入，因此返回 <c>InvalidRequest</c>
    /// （写半个字请用 <see cref="IPlcClient.WriteBytesAsync"/> 传入偶数长度，或写位用 <see cref="IPlcClient.WriteBitsAsync"/>）。
    /// </summary>
    /// <param name="client">客户端。</param>
    /// <param name="address">地址，例如 "DB1.DBB0"。</param>
    /// <param name="value">要写入的字节。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果。</returns>
    public static Task<CommResult> WriteByteAsync(this IPlcClient client, string address, byte value, CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        return client.WriteBytesAsync(address, new[] { value }, cancellationToken);
    }

    /// <summary>读取一个位：位软元件取 1 点；字软元件（可带位号）取覆盖位的字中的对应位；字节软元件取覆盖的字节中的对应位。</summary>
    /// <param name="client">客户端。</param>
    /// <param name="address">地址，例如 "M0"、"D100.5" 或 "B100.3"。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>位的值。</returns>
    public static async Task<CommResult<bool>> ReadBoolAsync(this IPlcClient client, string address, CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        CommResult<bool[]> bits = await client.ReadBitsAsync(address, 1, cancellationToken).ConfigureAwait(false);
        return bits.IsSuccess ? CommResult<bool>.Success(bits.Data![0]) : bits.As<bool>();
    }

    /// <summary>写入一个位（规则同 <c>WriteBitsAsync</c>，包括原生位写入与字软元件的读-改-写开关）。</summary>
    /// <param name="client">客户端。</param>
    /// <param name="address">地址，例如 "M0"、"D100.5" 或 "B100.3"。</param>
    /// <param name="value">要写入的值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果。</returns>
    public static async Task<CommResult> WriteBoolAsync(this IPlcClient client, string address, bool value, CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        return await client.WriteBitsAsync(address, new[] { value }, cancellationToken).ConfigureAwait(false);
    }

    // 字符串扩展要知道软元件是否只能整字访问：单位取 ParseAddress（同步、不产生 I/O），地址非法时原样透传解析器的失败。
    private static CommResult<bool> RequiresWholeWords(IPlcClient client, string address)
    {
        CommResult<PlcAddress> parsed = client.ParseAddress(address);
        return parsed.IsSuccess ? CommResult<bool>.Success(parsed.Data!.Unit != PlcAddressUnit.Byte) : parsed.As<bool>();
    }

    // 把编码后的字节扩展为 destinationLength 个字节，多出的部分保持 0x00；缩短由调用方在编码长度检查里拒绝。
    private static byte[] Expand(byte[] encoded, int destinationLength)
    {
        byte[] data = new byte[destinationLength];
        encoded.CopyTo(data, 0);
        return data;
    }
}
