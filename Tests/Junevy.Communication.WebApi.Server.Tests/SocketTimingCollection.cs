namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 需要真实套接字的测试集合（与其它项目的约定一致）。
///
/// 原因：这些用例启动真实的 Kestrel 监听，并发执行时线程池调度延迟会让基于毫秒的断言偶发失败。
/// xUnit 保证同一集合内的用例串行执行；纯内存的单元测试仍然并行。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = false)]
public sealed class SocketTimingCollection
{
    /// <summary>集合名称。</summary>
    public const string Name = "SocketTiming";
}
