using System.Buffers;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// 3E / 4E 以太网帧（手册 p.42–44）。3E 与 4E 共用同一个编解码类型：4E 只在副头部后多出 2 字节序列号与固定的 0000。
/// 请求的长度字段 = 监视定时器 + 请求数据的长度（ASCII 按字符计）；应答的长度字段 = 结束码与数据的长度。访问路径的应答回显不校验（手册 p.44）。
/// </summary>
internal sealed class McEthernetFrame
{
    private const ushort FixedSerialSuffix = 0x0000;  // 4E 序列号之后的固定值（手册 p.42）

    private readonly McFrameType frameType;
    private readonly McFieldCodec codec;

    /// <summary>创建 3E 或 4E 帧的编解码器。</summary>
    /// <param name="frameType">3E 或 4E。</param>
    /// <param name="encoding">二进制或 ASCII。</param>
    public McEthernetFrame(McFrameType frameType, McEncoding encoding)
    {
        if (frameType is not (McFrameType.Frame3E or McFrameType.Frame4E))
            throw new ArgumentOutOfRangeException(nameof(frameType), frameType, "The Ethernet frame must be 3E or 4E.");

        this.frameType = frameType;
        codec = McFieldCodec.For(encoding);
    }

    private bool Is4E => frameType == McFrameType.Frame4E;

    /// <summary>访问路径的字段宽度：网络号 1、PC 号 1、I/O 号 2、模块站号 1（手册 p.42）。</summary>
    private int RouteSize => 3 * codec.UInt8Size + codec.UInt16Size;

    /// <summary>写入请求帧。</summary>
    /// <param name="output">输出缓冲。</param>
    /// <param name="serial">4E 的序列号（3E 忽略）。</param>
    /// <param name="monitoringTimer">监视定时器（250 ms 为单位）。</param>
    /// <param name="route">访问路径。</param>
    /// <param name="body">请求数据（命令、子命令与参数，已按编码写好）。</param>
    public void WriteRequest(IBufferWriter<byte> output, ushort serial, ushort monitoringTimer, McAccessRoute route, ReadOnlySpan<byte> body)
    {
        using var frame = new MemoryStream();
        codec.WriteSubheader(frame, Is4E ? McCommands.Request4E : McCommands.Request3E);
        if (Is4E)
        {
            codec.WriteUInt16(frame, serial);
            codec.WriteUInt16(frame, FixedSerialSuffix);
        }

        WriteRoute(frame, route);
        codec.WriteUInt16(frame, checked((ushort)(codec.UInt16Size + body.Length)));
        codec.WriteUInt16(frame, monitoringTimer);
        codec.WriteRaw(frame, body);
        McFrameOutput.CopyTo(frame, output);
    }

    /// <summary>解码应答帧。结束码非 0 时仍返回成功，携带结束码且数据为空；由 <see cref="McResponse.CheckEndCode"/> 转换为 RemoteError。</summary>
    /// <param name="frame">完整的应答帧。</param>
    /// <param name="serial">请求使用的序列号（3E 忽略）。</param>
    /// <param name="kind">应答数据的种类。</param>
    /// <param name="count">字数、点数或回环字节数。</param>
    /// <returns>解码结果；帧结构错误为 ProtocolViolation。</returns>
    public CommResult<McResponse> ReadResponse(ReadOnlySpan<byte> frame, ushort serial, McDataKind kind, int count)
    {
        ReadOnlySpan<byte> rest = frame;
        string? headerError = ReadHeader(ref rest, serial);
        if (headerError is not null)
            return McResponse.Violation(headerError);

        if (!codec.TryTakeUInt16(ref rest, out ushort length) || rest.Length != length)
            return McResponse.Violation("The MC response length field does not match the frame length.");

        if (!codec.TryTakeUInt16(ref rest, out ushort endCode))
            return McResponse.Violation("The MC response is too short to contain an end code.");

        return endCode != 0
            ? McResponse.Decoded(endCode, Array.Empty<byte>())
            : McResponseData.Read(codec, rest, kind, count);
    }

    /// <summary>读取副头部、（4E 的）序列号与固定字段、访问路径；返回错误描述，无错误时为 null。</summary>
    private string? ReadHeader(ref ReadOnlySpan<byte> rest, ushort serial)
    {
        if (!TryTakeSubheader(ref rest, out ushort subheader) || subheader != (Is4E ? McCommands.Response4E : McCommands.Response3E))
            return "The MC response subheader does not match the frame type.";

        if (Is4E)
        {
            if (!codec.TryTakeUInt16(ref rest, out ushort responseSerial) || responseSerial != serial)
                return "The MC response serial number does not match the request.";

            if (!McFieldCodec.TryTake(ref rest, codec.UInt16Size, out _))
                return "The MC response is too short for the 4E fixed field.";
        }

        return McFieldCodec.TryTake(ref rest, RouteSize, out _) ? null : "The MC response is too short for the access route.";
    }

    private bool TryTakeSubheader(ref ReadOnlySpan<byte> rest, out ushort value)
    {
        value = 0;
        return McFieldCodec.TryTake(ref rest, codec.UInt16Size, out ReadOnlySpan<byte> field) && codec.TryReadSubheader(field, out value);
    }

    private void WriteRoute(Stream frame, McAccessRoute route)
    {
        codec.WriteUInt8(frame, route.NetworkNumber);
        codec.WriteUInt8(frame, route.PcNumber);
        codec.WriteUInt16(frame, route.ModuleIoNumber);
        codec.WriteUInt8(frame, route.ModuleStationNumber);
    }
}
