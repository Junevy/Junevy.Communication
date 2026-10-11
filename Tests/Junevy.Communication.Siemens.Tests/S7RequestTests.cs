using Junevy.Communication.Siemens.Protocol;
using Junevy.Communication.Testing;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// S7 请求编码（计划 Task 7 第 1 条）：第 17.1 节 V-S7-1、V-S7-3、V-S7-5、V-S7-7、V-S7-9、V-S7-10 逐字节核对，
/// 外加 P4-D19 的 TSAP 计算、P4-D13 的请求 PDU 长度、17.1 节说明段的地址字段（字节号 × 8 + 位号），
/// 以及写数据项传输尺寸按单位选择（1 字 0x04、2 字 0x05）。
/// </summary>
public sealed class S7RequestTests
{
    private const ushort SetupReference = 0x0001;
    private const ushort ReadDbReference = 0x0002;
    private const ushort WriteReference = 0x0003;
    private const ushort WriteBitReference = 0x0004;
    private const ushort ReadMerkerReference = 0x0005;

    [Fact]
    public void V_S7_1_ConnectRequest_EncodesRack0Slot1WithPgTsap()
    {
        byte[] frame = CotpFrames.BuildConnectionRequest(Pg, Rack, Slot);

        Assert.Equal(V_S7_1, ToText(frame));
    }

    [Theory]
    [InlineData(Op, 0, 1, Cr_Op)]
    [InlineData(Basic, 1, 2, Cr_Basic_Rack1_Slot2)]
    public void P4_D19_RemoteTsap_IsConnectionTypeRackAndSlot(int connectionType, int rack, int slot, string expected)
    {
        byte[] frame = CotpFrames.BuildConnectionRequest(connectionType, rack, slot);

        Assert.Equal(expected, ToText(frame));
    }

    [Fact]
    public void V_S7_3_SetupCommunication_Requests960Bytes()
    {
        byte[] frame = S7SetupCommunication.BuildRequest(SetupReference);

        Assert.Equal(V_S7_3, ToText(frame));
    }

    [Fact]
    public void V_S7_5_ReadVar_ReadsFourBytesOfDb1AsByteTransport()
    {
        byte[] frame = S7ReadVar.BuildRequest(ReadDbReference, S7DeviceTable.DataBlock.AreaCode, 1, 0, 4);

        Assert.Equal(V_S7_5, ToText(frame));
    }

    [Fact]
    public void V_S7_10_ReadVar_ReadsOneByteOfMerkerArea()
    {
        byte[] frame = S7ReadVar.BuildRequest(ReadMerkerReference, S7DeviceTable.Merkers.AreaCode, 0, 10, 1);

        Assert.Equal(V_S7_10, ToText(frame));
    }

    [Fact]
    public void V_S7_7_WriteVar_WritesOneWordWithByteTransportSize()
    {
        byte[] frame = S7WriteVar.BuildRequest(WriteReference, S7DeviceTable.DataBlock.AreaCode, 1, 2, ToBytes("12 34"));

        Assert.Equal(V_S7_7, ToText(frame));
    }

    [Fact]
    public void Write_2Words_DataItemLengthCountsBits()
    {
        byte[] frame = S7WriteVar.BuildRequest(WriteReference, S7DeviceTable.DataBlock.AreaCode, 1, 2, ToBytes("12 34 56 78"));

        Assert.Equal(Write_2Words, ToText(frame));
    }

    [Fact]
    public void V_S7_9_WriteBit_WritesOneBitAtByte10Bit3()
    {
        byte[] frame = S7WriteVar.BuildBitRequest(WriteBitReference, S7DeviceTable.DataBlock.AreaCode, 1, 10, 3, true);

        Assert.Equal(V_S7_9, ToText(frame));
    }

    [Fact]
    public void V_S7_9_WriteBit_WritesZeroWhenTheValueIsFalse()
    {
        byte[] frame = S7WriteVar.BuildBitRequest(WriteBitReference, S7DeviceTable.DataBlock.AreaCode, 1, 10, 3, false);

        Assert.Equal(WriteBit_Zero, ToText(frame));
    }

    [Fact]
    public void EveryRequest_CarriesTpktLengthAndCotpDataTransfer()
    {
        byte[][] requests =
        {
            S7SetupCommunication.BuildRequest(SetupReference),
            S7ReadVar.BuildRequest(ReadDbReference, S7DeviceTable.DataBlock.AreaCode, 1, 0, 4),
            S7WriteVar.BuildRequest(WriteReference, S7DeviceTable.DataBlock.AreaCode, 1, 2, ToBytes("12 34")),
            S7WriteVar.BuildBitRequest(WriteBitReference, S7DeviceTable.DataBlock.AreaCode, 1, 10, 3, true),
        };

        foreach (byte[] frame in requests)
        {
            // TPKT 为 03 00 LL LL，长度大端且含 TPKT 自身（17.1 节说明段）；COTP DT 为 02 F0 80。
            Assert.Equal($"03 00 {frame.Length >> 8:X2} {frame.Length & 0xFF:X2}", ToText(frame.AsSpan(0, 4)));
            Assert.Equal("02 F0 80", ToText(frame.AsSpan(4, 3)));
        }
    }
}
