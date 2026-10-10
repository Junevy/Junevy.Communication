namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 需要真实套接字且对墙钟时间敏感的测试集合（与其它测试程序集中的同名集合相同；集合定义必须位于每个测试程序集内）。
/// xUnit 保证同一集合内的用例串行执行，因此建立真实 TCP / UDP 连接的用例集中于此，避免线程池调度延迟干扰时间预算。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = false)]
public sealed class SocketTimingCollection
{
    public const string Name = "SocketTiming";
}
