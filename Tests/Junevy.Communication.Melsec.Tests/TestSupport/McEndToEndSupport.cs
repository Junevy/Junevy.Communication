using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Melsec.Framing;
using Junevy.Communication.Plc;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 端到端测试的共用辅助（计划 Task 12）：设备端与客户端使用同一套分帧，等待条件（不使用 Thread.Sleep），
/// 以及类型化读写的往返检查（16 / 32 / 64 位、单双精度、字符串、位、字中的位）。
/// </summary>
internal static class McEndToEndSupport
{
    /// <summary>设备端的分帧：与 <see cref="McProtocolProfile"/> 为客户端配置的分帧相同。</summary>
    public static IFrameCodecFactory DeviceFraming(McClientOptions options)
        => McStreamFraming.Create(McProtocolSettings.From(options), new FramingOptions().MaxFrameLength);

    /// <summary>每 10 ms 检查一次条件，超时抛出 <see cref="TimeoutException"/>。</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int milliseconds)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The expected condition was not met in time.");

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>类型化读写的往返：每种类型写入后读回，结果必须与写入的值相同。</summary>
    public static async Task AssertTypedRoundTripAsync(IPlcClient client)
    {
        Assert.True((await client.WriteInt16Async("D100", -2)).IsSuccess);
        Assert.Equal((short)-2, (await client.ReadInt16Async("D100")).Data);

        Assert.True((await client.WriteInt32Async("D200", -123456789)).IsSuccess);
        Assert.Equal(-123456789, (await client.ReadInt32Async("D200")).Data);

        Assert.True((await client.WriteInt64Async("D210", 1234567890123L)).IsSuccess);
        Assert.Equal(1234567890123L, (await client.ReadInt64Async("D210")).Data);

        Assert.True((await client.WriteFloatAsync("D220", 1.5f)).IsSuccess);
        Assert.Equal(1.5f, (await client.ReadFloatAsync("D220")).Data);

        Assert.True((await client.WriteDoubleAsync("D230", 2.25d)).IsSuccess);
        Assert.Equal(2.25d, (await client.ReadDoubleAsync("D230")).Data);

        Assert.True((await client.WriteStringAsync("D240", "HELLO")).IsSuccess);
        Assert.Equal("HELLO", (await client.ReadStringAsync("D240", 6)).Data);

        Assert.True((await client.WriteBoolAsync("M100", true)).IsSuccess);
        Assert.True((await client.ReadBoolAsync("M100")).Data);
        Assert.False((await client.ReadBoolAsync("M101")).Data);

        // 字中的位：位号为十六进制，D250 的第 5 位为 1，第 4 位为 0。
        Assert.True((await client.WriteInt16Async("D250", 0x0020)).IsSuccess);
        Assert.True((await client.ReadBoolAsync("D250.5")).Data);
        Assert.False((await client.ReadBoolAsync("D250.4")).Data);
    }
}
