using System.Buffers;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Modbus.Protocol;

/// <summary>
/// RTU 帧（串口与 RTU over TCP / UDP）：站号 1 字节、PDU、CRC 2 字节（低字节在前）。
/// 分帧（按功能码确定帧长）由 <c>ModbusRtuFrameDecoder</c> 完成；这里只做封装与剥离，以及 RTU 站号范围的唯一定义。
/// </summary>
internal static class RtuAdu
{
    private const int UnitIdLength = 1;
    private const int CrcLength = 2;

    /// <summary>RTU 站号的下限：0 是广播，本库不做（P7-D4）。</summary>
    public const byte MinimumUnitId = 1;

    /// <summary>RTU 站号的上限：248–255 是保留值（Modbus over Serial Line 2.2 节）。</summary>
    public const byte MaximumUnitId = 247;

    /// <summary>站号是否可用于 RTU 请求（1–247）。</summary>
    /// <param name="unitId">站号。</param>
    /// <returns>在范围内返回 true。</returns>
    public static bool IsValidUnitId(byte unitId) => unitId >= MinimumUnitId && unitId <= MaximumUnitId;

    /// <summary>写入 RTU 帧：站号、PDU 与 CRC。</summary>
    /// <param name="output">目标。</param>
    /// <param name="unitId">站号。</param>
    /// <param name="pdu">PDU。</param>
    public static void Write(IBufferWriter<byte> output, byte unitId, ReadOnlySpan<byte> pdu)
    {
        var frame = new byte[UnitIdLength + pdu.Length + CrcLength];
        frame[0] = unitId;
        pdu.CopyTo(frame.AsSpan(UnitIdLength));

        int body = UnitIdLength + pdu.Length;
        ushort crc = ModbusCrc.Compute(frame.AsSpan(0, body));
        frame[body] = (byte)(crc & 0xFF);
        frame[body + 1] = (byte)(crc >> 8);

        BigEndianFields.WriteBytes(output, frame);
    }

    /// <summary>
    /// 校验并剥离 RTU 帧，返回 PDU。先校验 CRC，再校验站号（P7-D10）。
    /// </summary>
    /// <param name="frame">完整的 RTU 帧（含 CRC）。</param>
    /// <param name="expectedUnitId">请求的站号。</param>
    /// <returns>PDU 的副本；格式不符返回 <c>ProtocolViolation</c>。</returns>
    public static CommResult<byte[]> Unwrap(ReadOnlySpan<byte> frame, byte expectedUnitId)
    {
        const int minimumLength = UnitIdLength + 1 + CrcLength;
        if (frame.Length < minimumLength)
            return CommResult<byte[]>.Fail("The RTU frame is shorter than the minimum of 4 bytes.", CommErrorKind.ProtocolViolation);

        int body = frame.Length - CrcLength;
        ushort received = (ushort)(frame[body] | (frame[body + 1] << 8));
        if (ModbusCrc.Compute(frame.Slice(0, body)) != received)
            return CommResult<byte[]>.Fail("The RTU CRC does not match the frame.", CommErrorKind.ProtocolViolation);

        if (frame[0] != expectedUnitId)
        {
            return CommResult<byte[]>.Fail($"The response unit ID {frame[0]} does not match the request unit ID {expectedUnitId}.",
                                           CommErrorKind.ProtocolViolation);
        }

        return CommResult<byte[]>.Success(frame.Slice(UnitIdLength, body - UnitIdLength).ToArray());
    }
}
