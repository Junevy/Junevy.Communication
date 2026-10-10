using Junevy.Communication.Channels;
using Junevy.Communication.Plc;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 端到端（串口 4C，计划 Task 12）：<see cref="DuplexClientChannel"/> 与设备替身通过内存管道相连。
/// 格式 4（ASCII）与格式 5（二进制）各跑一遍和校验开与关；格式 5 的数据含大量 10H，检验附加码。
/// </summary>
public sealed class McEndToEnd4CTests
{
    [Theory(Timeout = 60000)]
    [InlineData(McEncoding.Ascii, true)]
    [InlineData(McEncoding.Ascii, false)]
    [InlineData(McEncoding.Binary, true)]
    [InlineData(McEncoding.Binary, false)]
    public async Task TypedReadsAndWritesRoundTrip(McEncoding encoding, bool sumCheck)
    {
        var options = new McClientOptions { FrameType = McFrameType.Frame4C, Encoding = encoding, SumCheck = sumCheck };
        var adapter = new McDeviceAdapter(McFrameType.Frame4C, encoding, sumCheck, new PlcMemory());
        DeviceSimulator simulator = new DeviceSimulator(McEndToEndSupport.DeviceFraming(options), adapter.Handle);
        using var stopDevice = new CancellationTokenSource();
        var components = McProtocolProfile.ConfigureStream(new ChannelComponents(), options);
        // 打开回调在连接完成之前被等待，因此设备替身在后台运行，回调本身立即完成。
        var channel = new DuplexClientChannel(new ClientChannelSettings(), components, pair =>
        {
            _ = simulator.RunAsync(pair.B, stopDevice.Token);
            return Task.CompletedTask;
        });
        using McClient client = new McClient(channel, options);

        try
        {
            Assert.True((await client.ConnectAsync()).IsSuccess);
            await McEndToEndSupport.AssertTypedRoundTripAsync(client);

            // 数据中的 10H（附加码）：字 1010H 与 0010H。
            Assert.True((await client.WriteUInt16Async("D300", 0x1010)).IsSuccess);
            Assert.Equal((ushort)0x1010, (await client.ReadUInt16Async("D300")).Data);
            Assert.True((await client.WriteUInt16Async("D301", 0x0010)).IsSuccess);
            Assert.Equal((ushort)0x0010, (await client.ReadUInt16Async("D301")).Data);

            // 大量 10H：64 个字 1010H 共 128 个 10H 字节，格式 5 中每一个都要附加码转义。
            short[] tens = Enumerable.Repeat((short)0x1010, 64).ToArray();
            Assert.True((await client.WriteInt16Async("D400", tens)).IsSuccess);
            Assert.Equal(tens, (await client.ReadInt16Async("D400", 64)).Data);
        }
        finally
        {
            stopDevice.Cancel();
        }
    }
}
