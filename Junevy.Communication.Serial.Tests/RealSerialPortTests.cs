using System.Collections.Concurrent;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using static Junevy.Communication.Serial.Tests.SerialTestHelpers;

namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 真实串口对测试（计划 14.3）。需要环境变量 <c>JUNEVY_SERIAL_PAIR</c> 指定两个互连端口；未设置时全部跳过（显示为 Skipped）。
/// 本类是唯一会打开真实端口的测试类；其它测试一律使用假端口。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class RealSerialPortTests
{
    [SerialPairFact(Timeout = 180000)]
    public async Task RealPair_RoundTrip_9600_And_115200()
    {
        (string first, string second) = SerialPairEnvironment.Read();
        foreach (int baud in new[] { 9600, 115200 })
        {
            await using var client = new SerialChannel(RealConfig(first, baud, DelimiterFraming()));
            await using var device = new SerialChannel(RealConfig(second, baud, DelimiterFraming()));

            // 设备端把每个请求加上 "ACK:" 前缀后回复；客户端串行发出请求，因此每个请求恰好对应一个回复。
            var replies = new ConcurrentQueue<Task<CommResult>>();
            device.FrameReceived += (sender, args) => replies.Enqueue(device.SendAsync(Concat(Ascii("ACK:"), args.Data)));

            CommResult deviceConnect = await WithinAsync(device.ConnectAsync(), 10000);
            Assert.True(deviceConnect.IsSuccess, deviceConnect.ToString());
            CommResult clientConnect = await WithinAsync(client.ConnectAsync(), 10000);
            Assert.True(clientConnect.IsSuccess, clientConnect.ToString());

            for (int i = 0; i < 10; i++)
            {
                byte[] payload = Ascii($"REQ-{baud}-{i:D2}");
                CommResult<byte[]> reply = await WithinAsync(client.RequestAsync(payload, new RequestOptions { Timeout = 5000 }), 10000);
                Assert.True(reply.IsSuccess, $"{baud} baud, round {i}: {reply}");
                Assert.Equal(Concat(Ascii("ACK:"), payload), reply.Data);
            }

            foreach (Task<CommResult> sent in replies)
            {
                CommResult result = await WithinAsync(sent, 5000);
                Assert.True(result.IsSuccess, result.ToString());
            }
        }
    }

    [SerialPairFact(Timeout = 300000)]
    public async Task RealPair_IdleGap_FramesIntact()
    {
        (string first, string second) = SerialPairEnvironment.Read();

        // 默认分帧 IdleGap（20 ms）：100 帧之间有 50 ms 静默，每一帧都必须完整交付，且不与相邻帧合并或拆分。
        await using var sender = new SerialChannel(RealConfig(first, 115200, framing: null));
        await using var receiver = new SerialChannel(RealConfig(second, 115200, framing: null));
        var sink = new FrameSink(receiver);

        CommResult receiverConnect = await WithinAsync(receiver.ConnectAsync(), 10000);
        Assert.True(receiverConnect.IsSuccess, receiverConnect.ToString());
        CommResult senderConnect = await WithinAsync(sender.ConnectAsync(), 10000);
        Assert.True(senderConnect.IsSuccess, senderConnect.ToString());

        const int count = 100;
        var expected = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            byte[] payload = Ascii($"FRAME-{i:D3}-PAYLOAD-0123456789");
            expected.Add(payload);
            CommResult sent = await WithinAsync(sender.SendAsync(payload), 5000);
            Assert.True(sent.IsSuccess, sent.ToString());
            await Task.Delay(50);
        }

        await WaitUntilAsync(() => sink.Count >= count, 60000);
        IReadOnlyList<byte[]> frames = sink.Snapshot();
        Assert.Equal(count, frames.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal(expected[i], frames[i]);
    }

    // 真实端口的配置：超时取较宽的值，避免驱动延迟造成误判。framing 为 null 时保留默认分帧（IdleGap）。
    private static SerialChannelConfig RealConfig(string port, int baudRate, FramingOptions? framing)
    {
        var config = new SerialChannelConfig
        {
            PortName = port,
            BaudRate = baudRate,
            OpenTimeout = 5000,
            SendTimeout = 5000,
            RequestTimeout = 5000,
        };

        if (framing != null)
            config.Framing = framing;

        return config;
    }
}
