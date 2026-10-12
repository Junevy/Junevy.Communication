using Junevy.Communication.Testing;

namespace Junevy.Communication.Mqtt.Tests.TestSupport;

/// <summary>重连用例共用的等待。两个测试类都要用，因此放在这里而不是各写一份。</summary>
internal static class MqttReconnectWait
{
    /// <summary>
    /// 等一次重连完成。Broker 在同一个端口还活着时重连非常快，<c>State</c> 可能一闪而过，
    /// 因此先等 Broker 真的看到下一个 CONNECT（确定性的证据），再等状态回到 <c>Connected</c>。
    /// </summary>
    /// <param name="client">被测客户端。</param>
    /// <param name="broker">Broker。</param>
    /// <param name="connectsBefore">断开之前 Broker 已经收到的 CONNECT 数。</param>
    /// <returns>重连完成。</returns>
    public static async Task ReconnectedAsync(MqttClient client, TestBroker broker, int connectsBefore)
    {
        await Polling.WaitUntilAsync(() => broker.Connects.Count > connectsBefore);
        await Polling.WaitUntilAsync(() => client.State == MqttConnectionState.Connected);
    }
}