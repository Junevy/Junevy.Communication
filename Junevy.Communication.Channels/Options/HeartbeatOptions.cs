namespace Junevy.Communication.Channels;

/// <summary>
/// 应用层心跳配置（设计文档第 5.4 节）。
/// </summary>
public sealed class HeartbeatOptions
{
    /// <summary>是否启用应用层心跳，默认 false。</summary>
    public bool Enabled { get; set; }

    /// <summary>心跳间隔（毫秒），默认 5000。</summary>
    public int Interval { get; set; } = 5000;

    /// <summary>单次探测的超时（毫秒），默认 2000。</summary>
    public int Timeout { get; set; } = 2000;

    /// <summary>连续失败多少次判定连接死亡，默认 3。</summary>
    public int MaxFailures { get; set; } = 3;

    /// <summary>仅在空闲时发送心跳（有收发流量时跳过），默认 true。</summary>
    public bool OnlyWhenIdle { get; set; } = true;

    /// <summary>内置探测的心跳内容（文本，或以 <c>hex:</c> 开头）。</summary>
    public string? Payload { get; set; }

    /// <summary>内置探测期望的应答（整帧精确匹配，D14）；为空时发送成功即视为健康。</summary>
    public string? ExpectedReply { get; set; }
}
