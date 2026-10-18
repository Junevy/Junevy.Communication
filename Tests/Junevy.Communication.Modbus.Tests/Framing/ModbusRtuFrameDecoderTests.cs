using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Modbus.Framing;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.Framing;

/// <summary>
/// RTU 分帧（P7-D8，第 21.4 节的长度表）：按应答功能码确定帧长，收全后以 CRC 裁决；未知功能码或 CRC 不符时丢弃 1 个字节后重新同步。
/// 请求与应答共用同一条线路时（本地回显），只交出应答。
/// </summary>
public sealed class ModbusRtuFrameDecoderTests
{
    private const string ReadResponse = "01 03 02 00 01 79 84";                  // V-R-3
    private const string WriteResponse = "01 03 06 02 2B 00 00 00 64 05 7A";     // V-R-6resp
    private const string ReadRequest = "01 03 00 00 00 0A C5 CD";                // V-R-1（请求）

    private static List<byte[]> DecodeAll(ModbusRtuFrameDecoder decoder, byte[] bytes)
    {
        var buffer = new ReadOnlySequence<byte>(bytes);
        var frames = new List<byte[]>();
        while (decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> frame))
            frames.Add(frame.ToArray());

        return frames;
    }

    [Fact(Timeout = 20000)]
    public void SingleResponse_IsDecodedAsOneFrame()
    {
        List<byte[]> frames = DecodeAll(new ModbusRtuFrameDecoder(), HexVector.ToBytes(ReadResponse));

        Assert.Equal(new[] { HexVector.ToBytes(ReadResponse) }, frames);
    }

    [Fact(Timeout = 20000)]
    public void ArrivingByteByByte_WaitsUntilTheFrameIsComplete()
    {
        byte[] frame = HexVector.ToBytes(ReadResponse);
        var decoder = new ModbusRtuFrameDecoder();

        for (int length = 1; length < frame.Length; length++)
        {
            var partial = new ReadOnlySequence<byte>(frame.AsMemory(0, length));
            Assert.False(decoder.TryDecode(ref partial, out _), $"decoded early after {length} byte(s)");
        }

        var complete = new ReadOnlySequence<byte>(frame);
        Assert.True(decoder.TryDecode(ref complete, out ReadOnlySequence<byte> decoded));
        Assert.Equal(frame, decoded.ToArray());
    }

    [Fact(Timeout = 20000)]
    public void TwoFramesInOneBuffer_AreDecodedInOrder()
    {
        byte[] bytes = HexVector.ToBytes(ReadResponse + " " + WriteResponse);

        List<byte[]> frames = DecodeAll(new ModbusRtuFrameDecoder(), bytes);

        Assert.Equal(new[] { HexVector.ToBytes(ReadResponse), HexVector.ToBytes(WriteResponse) }, frames);
    }

    [Fact(Timeout = 20000)]
    public void ExceptionResponse_IsFiveBytes()
    {
        // V-R-15：异常应答 01 83 02 C0 F1，长度表给出 5 字节。
        List<byte[]> frames = DecodeAll(new ModbusRtuFrameDecoder(), HexVector.ToBytes("01 83 02 C0 F1"));

        Assert.Equal(new[] { HexVector.ToBytes("01 83 02 C0 F1") }, frames);
    }

    [Fact(Timeout = 20000)]
    public void NoiseBeforeTheFrame_IsDiscarded()
    {
        List<byte[]> frames = DecodeAll(new ModbusRtuFrameDecoder(), HexVector.ToBytes("00 00 00 " + ReadResponse));

        Assert.Equal(new[] { HexVector.ToBytes(ReadResponse) }, frames);
    }

    [Fact(Timeout = 20000)]
    public void CrcErrorFollowedByAValidFrame_OnlyTheValidFrameIsDelivered()
    {
        // 把 V-R-3 的一个数据位翻转，CRC 不再匹配；后面紧跟一个正确的应答。
        byte[] corrupted = HexVector.Mutated(ReadResponse, 4, 0x00);
        byte[] bytes = corrupted.Concat(HexVector.ToBytes(WriteResponse)).ToArray();

        List<byte[]> frames = DecodeAll(new ModbusRtuFrameDecoder(), bytes);

        Assert.Equal(new[] { HexVector.ToBytes(WriteResponse) }, frames);
    }

    [Fact(Timeout = 20000)]
    public void UnknownFunctionCode_IsDiscardedAndTheFollowingFrameIsDelivered()
    {
        List<byte[]> frames = DecodeAll(new ModbusRtuFrameDecoder(), HexVector.ToBytes("01 2B 00 00 " + ReadResponse));

        Assert.Equal(new[] { HexVector.ToBytes(ReadResponse) }, frames);
    }

    [Fact(Timeout = 20000)]
    public void LocalEcho_TheRequestIsDroppedAndOnlyTheResponseIsDelivered()
    {
        List<byte[]> frames = DecodeAll(new ModbusRtuFrameDecoder(), HexVector.ToBytes(ReadRequest + " " + ReadResponse));

        Assert.Equal(new[] { HexVector.ToBytes(ReadResponse) }, frames);
    }

    [Fact(Timeout = 20000)]
    public void DeclaredLengthBeyondTheMaximum_IsAFrameDecodeException()
    {
        // 字节数 253 使帧长达到 258，超过 RTU ADU 的 256 字节上限。
        var decoder = new ModbusRtuFrameDecoder();
        var buffer = new ReadOnlySequence<byte>(HexVector.ToBytes("01 03 FD"));

        Assert.Throws<FrameDecodeException>(() => decoder.TryDecode(ref buffer, out _));
    }
}
