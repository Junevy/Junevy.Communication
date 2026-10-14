using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 关联键（System Bytes）的取键规则（P51-D7、Q34、计划 Task 4）：请求键覆盖所有数据消息与
/// <c>Select.req</c> / <c>Linktest.req</c>；应答键**只**给应答类消息，因此对方的主消息即使
/// System Bytes 恰好等于我方在途请求的号，也不会被当成应答。
/// </summary>
/// <remarks>
/// 每个用例都显式写出两个标志：P51-D7 的规则是"哪些消息有请求键、哪些有应答键"两张表，
/// 用默认值省事就会把其中一张表漏测。
/// </remarks>
public sealed class HsmsKeyExtractorTests
{
    private static readonly HsmsKeyExtractor Extractor = new HsmsKeyExtractor();

    [Fact]
    public void EveryDataMessageCarriesARequestKeyButOnlyEvenFunctionsCarryAResponseKey()
    {
        // S1F1 W（Function 1，主消息）：有请求键，没有应答键。
        AssertKey(3, "00 00 00 0A 00 01 81 01 00 00 00 00 00 03", isRequest: true, isResponse: false);

        // S1F2（Function 2，应答）：两种键都有。
        AssertKey(3, "00 00 00 0A 00 01 01 02 00 00 00 00 00 03", isRequest: true, isResponse: true);
    }

    [Fact]
    public void SelectAndLinktestRequestsCarryARequestKey()
    {
        AssertKey(1, "00 00 00 0A FF FF 00 00 00 01 00 00 00 01", isRequest: true, isResponse: false);
        AssertKey(7, "00 00 00 0A FF FF 00 00 00 05 00 00 00 07", isRequest: true, isResponse: false);
    }

    [Fact]
    public void DeselectAndSeparateRequestsCarryNoKeyAtAll()
    {
        // HSMS-SS 不用 Deselect，Separate 是单向通知，两者都不等应答，也就不该占一个在途事务。
        AssertKey(9, "00 00 00 0A FF FF 00 00 00 03 00 00 00 09", isRequest: false, isResponse: false);
        AssertKey(8, "00 00 00 0A FF FF 00 00 00 09 00 00 00 08", isRequest: false, isResponse: false);
    }

    [Theory]
    [InlineData("00 00 00 0A FF FF 00 00 00 02 00 00 00 01")]
    [InlineData("00 00 00 0A FF FF 00 00 00 04 00 00 00 04")]
    [InlineData("00 00 00 0A FF FF 00 00 00 06 00 00 00 07")]
    [InlineData("00 00 00 0A 00 01 00 04 00 07 00 00 00 02")]
    public void ControlResponsesAndRejectCarryAResponseKeyOnly(string hex)
    {
        // *.rsp 与 Reject.req 只会认领在途请求，不会自己发起事务。
        AssertKey(KeyOf(hex), hex, isRequest: false, isResponse: true);
    }

    [Theory]
    [InlineData("00 00 00 0A FF FF 00 00 05 02 00 00 00 01")]
    [InlineData("00 00 00 0A 00 01 01 02 05 00 00 00 00 03")]
    public void AFrameWithAnUnsupportedPTypeCarriesNoKeyAtAll(string hex)
    {
        // PType ≠ 0 的帧不属于 SECS-II：既不能认领在途请求（哪怕它的 SType 像 Select.rsp、Function 像应答），
        // 也不占在途事务；它作为主动消息到达派发，由派发回 Reject.req 原因 2（P51-D12）。
        AssertKey(KeyOf(hex), hex, isRequest: false, isResponse: false);
    }

    [Fact]
    public void APrimaryMessageWhoseSystemBytesCollidesWithAnInFlightRequestIsNotAKeyedReply()
    {
        // Q34 的核心：对方的主消息 System Bytes 恰好等于我方在途请求的号。
        // 取不到应答键，因此 PendingRequestTable 认领不到它，它会作为主动消息派发出去。
        byte[] ourRequest = HsmsMessage.Write(SecsMessage.Create(5, 1, replyExpected: true, body: SecsItem.U4(1001)), 1, 0x2A).ToArray();
        byte[] theirPrimary = HsmsMessage.Write(SecsMessage.Create(7, 13, replyExpected: true, body: SecsItem.U4(1)), 7, 0x2A).ToArray();

        Assert.True(Extractor.TryGetRequestKey(ourRequest, out long ourKey));
        Assert.Equal(0x2A, ourKey);
        Assert.False(Extractor.TryGetResponseKey(theirPrimary, out long theirKey));
        Assert.Equal(0, theirKey);
    }

    [Fact]
    public void AShortOrEmptyFrameCarriesNoKey()
    {
        Assert.False(Extractor.TryGetRequestKey(ReadOnlySpan<byte>.Empty, out _));
        Assert.False(Extractor.TryGetResponseKey(ReadOnlySpan<byte>.Empty, out _));
        Assert.False(Extractor.TryGetRequestKey(HexVector.ToBytes("00 00 00 0A FF FF 00 00 00 01 00 00"), out _));
        Assert.False(Extractor.TryGetResponseKey(HexVector.ToBytes("00 00 00 0A FF FF 00 00 00 01 00 00"), out _));
    }

    [Fact]
    public void SystemBytesUseOnlyTheLowSixteenBitsAndKeepMoving()
    {
        uint first = HsmsSystemBytes.Next();
        uint second = HsmsSystemBytes.Next();

        Assert.NotEqual(first, second);
        Assert.InRange(second - first, 1u, 0xFFFFu);
        Assert.InRange(first, 0u, 0xFFFFu);
    }

    private static void AssertKey(uint expected, string hex, bool isRequest, bool isResponse)
    {
        byte[] frame = HexVector.ToBytes(hex);

        Assert.Equal(isRequest, Extractor.TryGetRequestKey(frame, out long requestKey));
        Assert.Equal(isResponse, Extractor.TryGetResponseKey(frame, out long responseKey));

        if (isRequest)
            Assert.Equal(expected, (uint)requestKey);
        if (isResponse)
            Assert.Equal(expected, (uint)responseKey);
    }

    private static uint KeyOf(string hex)
    {
        byte[] frame = HexVector.ToBytes(hex);
        return (uint)((frame[10] << 24) | (frame[11] << 16) | (frame[12] << 8) | frame[13]);
    }
}