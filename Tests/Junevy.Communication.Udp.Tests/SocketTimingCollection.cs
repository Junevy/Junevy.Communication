namespace Junevy.Communication.Udp.Tests;

/// <summary>
/// 需要真实套接字且对墙钟时间敏感的测试集合（与 Channels.Tests、Tcp.Tests 的同名集合相同的约定，计划第 1 节第 5 条）。
/// 同一集合内的用例串行执行，因此建立真实数据报收发的用例不会互相抢占端口与线程池；纯配置测试不在此集合中，仍可并行。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = false)]
public sealed class SocketTimingCollection
{
    public const string Name = "SocketTiming";
}
