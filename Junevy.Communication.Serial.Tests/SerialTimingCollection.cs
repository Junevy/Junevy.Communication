namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 含时间相关断言（超时、静默分帧、半帧计时、真实端口）的测试集合，与 Channels.Tests、Tcp.Tests 的同名约定相同（计划第 1 节第 5 条）。
/// 同一集合内的用例串行执行，因此时间断言不会因并行测试抢占线程池而偶发失败；纯配置测试不在此集合中，仍可并行。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = false)]
public sealed class SocketTimingCollection
{
    public const string Name = "SocketTiming";
}
