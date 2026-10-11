namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 连接驱动与监督器的一次性绑定（P25 Task 11，补审第 1 节）：字节流与数据报驱动共用同一段绑定规则，驱动只保留一行转发。
/// </summary>
internal static class SupervisorBinding
{
    /// <summary>
    /// 把监督器绑定到驱动的槽位；只能成功一次，且必须在任何打开之前调用。
    /// </summary>
    /// <param name="slot">驱动保存监督器的字段。</param>
    /// <param name="owner">监督器。</param>
    /// <exception cref="ArgumentNullException">监督器为 null。</exception>
    /// <exception cref="InvalidOperationException">已经绑定。</exception>
    public static void Attach(ref ConnectionSupervisor? slot, ConnectionSupervisor owner)
    {
        if (owner == null)
            throw new ArgumentNullException(nameof(owner));
        if (slot != null)
            throw new InvalidOperationException("The connection driver is already attached to a supervisor.");

        slot = owner;
    }
}
