namespace Junevy.Communication.Siemens;

/// <summary>
/// S7 的连接类型（P4-D19）：写入 COTP 连接请求的远端 TSAP，取值沿用 Snap7 的约定。
/// 部分 CPU 只允许其中一类连接，握手时的设备侧错误通常就是这个原因。
/// </summary>
public enum S7ConnectionType
{
    /// <summary>编程器连接。</summary>
    PG = 1,

    /// <summary>操作员连接。</summary>
    OP = 2,

    /// <summary>基本连接（S7-200 等不支持完整路由的设备）。</summary>
    Basic = 3,
}
