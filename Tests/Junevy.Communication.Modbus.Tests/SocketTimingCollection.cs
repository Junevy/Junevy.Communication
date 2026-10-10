namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// 需要真实套接字且对墙钟时间敏感的测试集合。
    ///
    /// 原因：这些用例都会建立真实 TCP 连接（部分还循环 20 次新建数百个套接字），如果与其它
    /// 用例并行执行，线程池调度延迟会让基于毫秒预算的断言（例如"服务端不应答时应在 ReadTimeout
    /// 内以 Timeout 返回"）偶发失败。xUnit 保证同一集合内的用例串行执行，因此把这些类集中到
    /// 一个集合，既消除互相干扰，又不影响纯内存单元测试之间的并行。
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = false)]
    public sealed class SocketTimingCollection
    {
        public const string Name = "SocketTiming";
    }
}