using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;

namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// 一条 MELSEC 连接上的帧交换（内部）：按快照选择 3E / 4E / 4C 帧，编码请求，解码并校验应答。
/// 4E 的序列号记录在最近一次编码的请求中，应答按它校验。客户端与每个心跳探测各持有一个实例；序列号来自进程级计数器，不会撞号。
/// </summary>
internal sealed class McFrameExchange
{
    // 4E 序列号的进程级计数器（P2-D11）：本类型是唯一写这个字段的地方，客户端请求与心跳探测共用，同一通道上不会撞号。取低 16 位。
    private static readonly SequenceCounter SerialNumbers = new SequenceCounter();

    private readonly McProtocolSettings settings;
    private readonly McFieldCodec codec;
    private readonly McEthernetFrame? ethernet;
    private readonly Mc4CFrame? serialFrame;
    private ushort lastSerial;

    /// <summary>创建帧交换。</summary>
    /// <param name="settings">选项快照。</param>
    public McFrameExchange(McProtocolSettings settings)
    {
        this.settings = settings;
        codec = McFieldCodec.For(settings.Encoding);
        if (settings.Is4C)
            serialFrame = new Mc4CFrame(settings.Encoding, settings.SumCheck);
        else
            ethernet = new McEthernetFrame(settings.FrameType, settings.Encoding);
    }

    /// <summary>软元件编号的上限（含），由字段编码层给出。</summary>
    /// <param name="device">软元件。</param>
    /// <returns>编号上限。</returns>
    public int MaxDeviceNumber(McDevice device) => codec.MaxDeviceNumber(device);

    /// <summary>编码批量读请求（字单位或位单位）。</summary>
    public byte[] BatchRead(McDevice device, int number, int points, bool bitUnit)
        => Frame(McRequestBody.BatchRead(codec, bitUnit ? McCommands.BitUnit : McCommands.WordUnit, device, number, points));

    /// <summary>编码字单位批量写请求。</summary>
    public byte[] BatchWriteWords(McDevice device, int number, ReadOnlySpan<byte> littleEndianWords)
        => Frame(McRequestBody.BatchWriteWords(codec, device, number, littleEndianWords));

    /// <summary>编码位单位批量写请求。</summary>
    public byte[] BatchWriteBits(McDevice device, int number, ReadOnlySpan<bool> values)
        => Frame(McRequestBody.BatchWriteBits(codec, device, number, values));

    /// <summary>编码回环（0619）请求。</summary>
    public byte[] Loopback(ReadOnlySpan<byte> data) => Frame(McRequestBody.Loopback(codec, data));

    /// <summary>解码字单位读应答；结束码非 0 时失败（RemoteError，携带原始码）。</summary>
    public CommResult<byte[]> ReadWords(ReadOnlySpan<byte> frame, int count)
    {
        CommResult<McResponse> decoded = Decode(frame, McDataKind.Words, count);
        if (!decoded.IsSuccess)
            return decoded.As<byte[]>();

        McResponse response = decoded.Data!;
        CommResult end = response.CheckEndCode();
        return end.IsSuccess ? CommResult<byte[]>.Success(response.Data) : end.As<byte[]>();
    }

    /// <summary>解码位单位读应答（每点一个值）；结束码非 0 时失败。</summary>
    public CommResult<bool[]> ReadBits(ReadOnlySpan<byte> frame, int count)
    {
        CommResult<McResponse> decoded = Decode(frame, McDataKind.Bits, count);
        if (!decoded.IsSuccess)
            return decoded.As<bool[]>();

        McResponse response = decoded.Data!;
        CommResult end = response.CheckEndCode();
        return end.IsSuccess ? CommResult<bool[]>.Success(response.Bits) : end.As<bool[]>();
    }

    /// <summary>解码写应答；结束码非 0 时失败。</summary>
    public CommResult ReadWrite(ReadOnlySpan<byte> frame)
    {
        CommResult<McResponse> decoded = Decode(frame, McDataKind.None, 0);
        return decoded.IsSuccess ? decoded.Data!.CheckEndCode() : decoded.ToResult();
    }

    /// <summary>检查回环应答：结束码为 0，且回显与发送的数据一致。</summary>
    public CommResult CheckLoopback(ReadOnlySpan<byte> frame, byte[] payload)
    {
        CommResult<McResponse> decoded = Decode(frame, McDataKind.Loopback, payload.Length);
        if (!decoded.IsSuccess)
            return decoded.ToResult();

        McResponse response = decoded.Data!;
        CommResult end = response.CheckEndCode();
        if (!end.IsSuccess)
            return end;

        return response.Data.AsSpan().SequenceEqual(payload)
            ? CommResult.Success()
            : CommResult.Fail("The MC loopback reply does not match the payload.", CommErrorKind.ProtocolViolation);
    }

    private byte[] Frame(byte[] body)
    {
        if (settings.Is4C)
            return serialFrame!.BuildRequest(settings.SerialRoute, body);

        lastSerial = settings.Is4E ? SerialNumbers.NextUInt16() : (ushort)0;
        return ethernet!.BuildRequest(lastSerial, settings.MonitoringTimer, settings.EthernetRoute, body);
    }

    private CommResult<McResponse> Decode(ReadOnlySpan<byte> frame, McDataKind kind, int count)
        => settings.Is4C
            ? serialFrame!.ReadResponse(frame, kind, count)
            : ethernet!.ReadResponse(frame, lastSerial, kind, count);
}
