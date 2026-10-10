namespace Junevy.Communication.Core.Resilience;

/// <summary>
/// 退避策略：根据重试序号计算下一次尝试前的等待时间。实现必须线程安全。
/// </summary>
public interface IBackoffPolicy
{
    /// <summary>
    /// 计算第 <paramref name="attempt"/> 次重试前的等待时间。
    /// </summary>
    /// <param name="attempt">从 1 开始的重试序号。</param>
    /// <returns>该次重试前的等待毫秒数；返回 null 表示放弃。</returns>
    int? GetDelay(int attempt);
}
