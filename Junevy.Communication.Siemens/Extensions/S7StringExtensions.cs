using Junevy.Communication.Core.Results;
using Junevy.Communication.Siemens.Protocol;

namespace Junevy.Communication.Siemens;

/// <summary>
/// S7 的字符串读写（计划第 13 节第 3 条）：读取整个字段（头部 + 声明长度的字符），写入只发头部与实际字符，
/// 字段余下的字节由设备保持原值。头部的实际长度大于声明的最大长度、或大于调用方给出的 <c>maxLength</c> 时判为帧结构错误；
/// 值超长或 <c>maxLength</c> 超出字段合同（<c>String</c> 1–254、<c>WString</c> 1–16382）时返回 <c>InvalidRequest</c>，不发请求。
/// <c>String</c> 的字符用 <see cref="Plc.PlcDataFormat.StringEncoding"/>，<c>WString</c> 一律 UTF-16 大端。
/// </summary>
public static class S7StringExtensions
{
    /// <summary>读取 S7 的 <c>String</c> 字段。</summary>
    /// <param name="client">S7 客户端。</param>
    /// <param name="address">地址，例如 "DB1.DBB0"；不能带位号。</param>
    /// <param name="maxLength">字段声明的最大字符数，1–254。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>字符串；参数不合法时返回 <c>InvalidRequest</c>，字段头部自相矛盾时返回 <c>ProtocolViolation</c>。</returns>
    public static async Task<CommResult<string>> ReadS7StringAsync(this S7Client client, string address, int maxLength,
                                                                   CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        CommResult<int> field = S7StringFormat.FieldBytes(maxLength);
        if (!field.IsSuccess)
            return field.As<string>();

        CommResult<byte[]> bytes = await client.ReadBytesAsync(address, field.Data, cancellationToken).ConfigureAwait(false);
        return bytes.IsSuccess
            ? S7StringFormat.Decode(bytes.Data, maxLength, client.DataFormat.StringEncoding)
            : bytes.As<string>();
    }

    /// <summary>写入 S7 的 <c>String</c> 字段。</summary>
    /// <param name="client">S7 客户端。</param>
    /// <param name="address">地址，例如 "DB1.DBB0"；不能带位号。</param>
    /// <param name="value">要写入的字符串；为 null 时返回 <c>InvalidRequest</c>，空串只写头部（清空字段）。</param>
    /// <param name="maxLength">字段声明的最大字符数，1–254。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果；编码结果超过 <paramref name="maxLength"/> 时返回 <c>InvalidRequest</c> 且不发请求。</returns>
    public static async Task<CommResult> WriteS7StringAsync(this S7Client client, string address, string value, int maxLength,
                                                            CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        CommResult<byte[]> field = S7StringFormat.Encode(value, maxLength, client.DataFormat.StringEncoding);
        if (!field.IsSuccess)
            return field.ToResult();

        return await client.WriteBytesAsync(address, field.Data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取 S7 的 <c>WString</c> 字段。</summary>
    /// <param name="client">S7 客户端。</param>
    /// <param name="address">地址，例如 "DB1.DBB0"；不能带位号。</param>
    /// <param name="maxLength">字段声明的最大字符数，1–16382。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>字符串；失败分类同 <see cref="ReadS7StringAsync"/>。</returns>
    public static async Task<CommResult<string>> ReadS7WStringAsync(this S7Client client, string address, int maxLength,
                                                                    CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        CommResult<int> field = S7StringFormat.WideFieldBytes(maxLength);
        if (!field.IsSuccess)
            return field.As<string>();

        CommResult<byte[]> bytes = await client.ReadBytesAsync(address, field.Data, cancellationToken).ConfigureAwait(false);
        return bytes.IsSuccess ? S7StringFormat.DecodeWide(bytes.Data, maxLength) : bytes.As<string>();
    }

    /// <summary>写入 S7 的 <c>WString</c> 字段。</summary>
    /// <param name="client">S7 客户端。</param>
    /// <param name="address">地址，例如 "DB1.DBB0"；不能带位号。</param>
    /// <param name="value">要写入的字符串；为 null 时返回 <c>InvalidRequest</c>，空串只写头部（清空字段）。</param>
    /// <param name="maxLength">字段声明的最大字符数，1–16382。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果；字符数超过 <paramref name="maxLength"/> 时返回 <c>InvalidRequest</c> 且不发请求。</returns>
    public static async Task<CommResult> WriteS7WStringAsync(this S7Client client, string address, string value, int maxLength,
                                                             CancellationToken cancellationToken = default)
    {
        RequireClient(client);
        CommResult<byte[]> field = S7StringFormat.EncodeWide(value, maxLength);
        if (!field.IsSuccess)
            return field.ToResult();

        return await client.WriteBytesAsync(address, field.Data, cancellationToken).ConfigureAwait(false);
    }

    private static void RequireClient(S7Client client)
    {
        if (client is null)
            throw new ArgumentNullException(nameof(client));
    }
}
