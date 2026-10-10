using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// 解码后的 MELSEC 应答：结束码与规范化数据（P2-D5）。字与回环数据放在 <see cref="Data"/>，位数据放在 <see cref="Bits"/>（每点一个值）。
/// 结束码非 0 时两者都为空，错误信息不解析（手册 p.44）。
/// </summary>
internal sealed class McResponse
{
    private static readonly byte[] NoBytes = Array.Empty<byte>();
    private static readonly bool[] NoBits = Array.Empty<bool>();

    internal McResponse(ushort endCode, byte[] data, bool[] bits)
    {
        EndCode = endCode;
        Data = data;
        Bits = bits;
    }

    /// <summary>结束码，0 表示正常完成（手册 p.44）。</summary>
    public ushort EndCode { get; }

    /// <summary>字数据（每字 2 字节小端）或回环数据（原始字节）；没有此类数据时为空数组。</summary>
    public byte[] Data { get; }

    /// <summary>位数据（每点一个值）；没有位数据时为空数组。</summary>
    public bool[] Bits { get; }

    /// <summary>解码成功，携带字或回环数据。</summary>
    /// <param name="endCode">结束码（0 为正常）。</param>
    /// <param name="data">字或回环数据。</param>
    /// <returns>成功的解码结果。</returns>
    public static CommResult<McResponse> Decoded(ushort endCode, byte[] data)
        => CommResult<McResponse>.Success(new McResponse(endCode, data, NoBits));

    /// <summary>解码成功，携带位数据。</summary>
    /// <param name="bits">位数据。</param>
    /// <returns>成功的解码结果。</returns>
    public static CommResult<McResponse> DecodedBits(bool[] bits)
        => CommResult<McResponse>.Success(new McResponse(0, NoBytes, bits));

    /// <summary>帧结构错误（ProtocolViolation）。</summary>
    /// <param name="message">英文错误描述。</param>
    /// <returns>失败的解码结果。</returns>
    public static CommResult<McResponse> Violation(string message)
        => CommResult<McResponse>.Fail(message, CommErrorKind.ProtocolViolation);

    /// <summary>把结束码转换为结果：0 成功；否则为 RemoteError，<see cref="CommResult.ProtocolErrorCode"/> 携带原始码（P2-D12）。</summary>
    /// <returns>检查结果。</returns>
    public CommResult CheckEndCode()
        => EndCode == 0
            ? CommResult.Success()
            : CommResult.Fail($"MC end code 0x{EndCode:X4}.", CommErrorKind.RemoteError, EndCode);
}
