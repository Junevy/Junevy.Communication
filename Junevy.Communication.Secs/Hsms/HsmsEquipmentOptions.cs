namespace Junevy.Communication.Secs.Hsms;

/// <summary>
/// 设备端的选项（计划 Task 6、第 12 节）。全是简单属性，可以直接从 appsettings 绑定；
/// <see cref="HsmsEquipment.Create"/> 会校验并取快照，因此之后改这个对象不影响已建好的设备。
/// </summary>
public sealed class HsmsEquipmentOptions
{
    /// <summary>设备端在 HSMS 层的角色；默认 <see cref="HsmsConnectionMode.Passive"/>（监听）。</summary>
    public HsmsConnectionMode Mode { get; set; } = HsmsConnectionMode.Passive;

    /// <summary>
    /// 地址：<see cref="HsmsConnectionMode.Passive"/> 下是监听地址（null 表示所有地址），
    /// <see cref="HsmsConnectionMode.Active"/> 下是对端地址（必填，可以是 IP 或主机名）。被动端只接受 IP。
    /// </summary>
    public string? Host { get; set; }

    /// <summary>端口，1–65535；默认 5000（SEMI E37 的标准端口）。</summary>
    public int Port { get; set; } = 5000;

    /// <summary>设备号（HSMS 的会话号），0–32767。</summary>
    public ushort DeviceId { get; set; }

    /// <summary>T3，应答超时（毫秒）：每个带 W 位的主消息等应答的时限。</summary>
    public int T3 { get; set; } = 45000;

    /// <summary>T5，连接间隔（毫秒）：掉线后多久再连（Task 7 的主动端使用）。</summary>
    public int T5 { get; set; } = 10000;

    /// <summary>T6，控制事务超时（毫秒）：Select 与 Linktest 的时限。</summary>
    public int T6 { get; set; } = 5000;

    /// <summary>T7，未选中超时（毫秒）：连上之后多久还没 Select 就断开。</summary>
    public int T7 { get; set; } = 10000;

    /// <summary>T8，字符间隔超时（毫秒）：两批字节之间允许的最长间隔。</summary>
    public int T8 { get; set; } = 5000;

    /// <summary>Linktest 心跳间隔（毫秒）；0 表示不发 Linktest。</summary>
    public int LinktestInterval { get; set; }

    /// <summary>单条消息的最大长度（字节），至少 1024；默认 16 MiB。</summary>
    public int MaxMessageLength { get; set; } = 16 * 1024 * 1024;
}
