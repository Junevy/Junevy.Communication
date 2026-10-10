namespace Junevy.Communication.Plc;

/// <summary>
/// 地址型协议客户端的通用选项。协议选项类（例如 MELSEC 的 <c>McClientOptions</c>）继承它。
/// 客户端构造时校验并取快照（D5），之后修改选项对象不影响已创建的客户端。
/// </summary>
public class PlcClientOptions
{
    /// <summary>
    /// 可重试的失败（超时、连接断开、协议违规）在链路恢复后最多重试的次数，必须 ≥ 0。默认 0：库内快速失败，轮询循环本身就是重试。
    /// </summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>
    /// 重试前等待链路恢复的上限（毫秒），必须 ≥ 0。默认 1000。
    /// </summary>
    public int RetryInterval { get; set; } = 1000;

    /// <summary>
    /// 允许通过读-改-写写入字中的单个位（P2-D10）。默认 false：写字中的位返回 <c>NotSupported</c>；开启后每次写入记录一条 Warning。
    /// </summary>
    public bool AllowReadModifyWriteBits { get; set; }
}
