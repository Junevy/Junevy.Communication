namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 使用真实回环套接字的 SECS 用例集合（与 Siemens / Omron 同法）。集合内的类不并行，避免端口与计时互相干扰。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = false)]
public sealed class SocketTimingCollection
{
    /// <summary>集合名。</summary>
    public const string Name = "SocketTiming";
}
