using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// 解码后的 MELSEC 应答：结束码与规范化数据（P2-D5）。结束码非 0 时数据为空，错误信息不解析（手册 p.44）。
/// </summary>
internal sealed class McResponse
{
    internal McResponse(ushort endCode, byte[] data)
    {
        EndCode = endCode;
        Data = data;
    }

    /// <summary>结束码，0 表示正常完成（手册 p.44）。</summary>
    public ushort EndCode { get; }

    /// <summary>规范化数据；结束码非 0 或没有数据时为空数组。</summary>
    public byte[] Data { get; }

    /// <summary>把结束码转换为结果：0 成功；否则为 RemoteError，<see cref="CommResult.ProtocolErrorCode"/> 携带原始码（P2-D12）。</summary>
    /// <returns>检查结果。</returns>
    public CommResult CheckEndCode()
        => EndCode == 0
            ? CommResult.Success()
            : CommResult.Fail($"MC end code 0x{EndCode:X4}.", CommErrorKind.RemoteError, EndCode);
}
