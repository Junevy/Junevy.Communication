namespace Junevy.Communication.Secs.Hsms;

/// <summary>
/// 设备端在 HSMS 层扮演的角色（SEMI E37.1）。HSMS-SS 只有一方主动，因此它决定"谁来发起 Select"。
/// </summary>
public enum HsmsConnectionMode
{
    /// <summary>被动端：设备端监听，等主机来连并 Select（设备端通常是产线的从机）。</summary>
    Passive,

    /// <summary>主动端：设备端去连主机并发起 Select（设备端通常是上位机要连的采集器）。</summary>
    Active,
}
