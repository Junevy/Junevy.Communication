using Junevy.Communication.Testing;
using static Junevy.Communication.Serial.Tests.SerialTestHelpers;

namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 测试辅助 <see cref="SerialDevice"/> 的自检：串行化测试依赖它观察"同时在途的请求"，因此它必须能真实地观察到并发请求。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class SerialDeviceSelfTests
{
    [Fact(Timeout = 30000)]
    public async Task SerialDevice_CountsRequestsSentWithoutReply()
    {
        DuplexStreamPair pair = DuplexStreamPair.Create();
        var cancel = new CancellationTokenSource();
        try
        {
            var device = new SerialDevice(pair.B, DelimiterCodec(), request => request, replyDelayMilliseconds: 300);
            _ = device.RunAsync(cancel.Token);

            // 两帧连续发出：设备在第一帧的应答延迟结束之前已经收到两帧。
            await WriteRawAsync(pair.A, Ascii("ONE\r\nTWO\r\n"));
            await WaitUntilAsync(() => device.Received == 2, 3000);

            Assert.Equal(2, device.MaxOutstanding);
        }
        finally
        {
            cancel.Cancel();
            pair.Dispose();
            cancel.Dispose();
        }
    }
}
