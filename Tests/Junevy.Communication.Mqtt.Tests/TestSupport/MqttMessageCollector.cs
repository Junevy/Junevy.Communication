namespace Junevy.Communication.Mqtt.Tests.TestSupport;

/// <summary>
/// 挂在客户端上收集收到的消息（计划 P61-D8）：按到达顺序记下，并可等待某一条到达。
/// 线程安全——事件是在 SDK 的接收线程上触发的。
/// </summary>
public sealed class MqttMessageCollector
{
    private readonly MqttClient client;
    private readonly List<MqttMessage> received = new();

    public MqttMessageCollector(MqttClient client)
    {
        this.client = client;
        client.MessageReceived += (_, args) =>
        {
            lock (received)
            {
                received.Add(args.Message);
            }
        };
    }

    /// <summary>收到的消息，按到达顺序的副本。</summary>
    public IReadOnlyList<MqttMessage> Messages
    {
        get
        {
            lock (received)
            {
                return received.ToArray();
            }
        }
    }

    /// <summary>收到过多少条消息。</summary>
    public int Count
    {
        get
        {
            lock (received)
            {
                return received.Count;
            }
        }
    }

    /// <summary>等待至少收到 <paramref name="count"/> 条消息。</summary>
    /// <param name="count">期望的条数。</param>
    /// <param name="milliseconds">时限（毫秒）。</param>
    /// <returns>等待完成。</returns>
    public Task WaitForCountAsync(int count, int milliseconds = 10000) =>
        Junevy.Communication.Testing.Polling.WaitUntilAsync(() => Count >= count, milliseconds);

    /// <summary>在一定时间内确认"没有再收到消息"。</summary>
    /// <param name="milliseconds">观察时长（毫秒）。</param>
    /// <returns>观察完成。</returns>
    public async Task ExpectSilenceAsync(int milliseconds = 300)
    {
        int before = Count;
        await Task.Delay(milliseconds);
        Assert.Equal(before, Count);
    }
}