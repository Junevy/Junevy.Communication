namespace Junevy.Communication.Modbus.Tests;

/// <summary>
/// 需要真实套接字的测试集合（与通道族测试项目的约定一致）。
///
/// 原因：这些用例建立真实的 UDP 套接字，如果与其它用例并行执行，线程池调度延迟会让基于毫秒预算的断言偶发失败。
/// xUnit 保证同一集合内的用例串行执行，因此把这些类集中到一个集合，既消除互相干扰，又不影响纯内存单元测试之间的并行。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = false)]
public sealed class SocketTimingCollection
{
    public const string Name = "SocketTiming";
}
