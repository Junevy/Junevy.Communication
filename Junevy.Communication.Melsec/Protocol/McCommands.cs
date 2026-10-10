namespace Junevy.Communication.Melsec.Protocol;

/// <summary>MC 协议的命令、子命令与副头部（手册页码见各项）。</summary>
internal static class McCommands
{
    /// <summary>批量读出（字与位单位，手册 p.89、p.91）。</summary>
    public const ushort BatchRead = 0x0401;

    /// <summary>批量写入（字 p.94、位 p.96）。</summary>
    public const ushort BatchWrite = 0x1401;

    /// <summary>回环测试（手册 p.184）。</summary>
    public const ushort Loopback = 0x0619;

    /// <summary>字单位子命令（Q/L 兼容，手册 p.89）。</summary>
    public const ushort WordUnit = 0x0000;

    /// <summary>位单位子命令（手册 p.91）。</summary>
    public const ushort BitUnit = 0x0001;

    /// <summary>回环测试的子命令（手册 p.184）。</summary>
    public const ushort LoopbackSubcommand = 0x0000;

    /// <summary>3E 请求副头部（手册 p.42）。</summary>
    public const ushort Request3E = 0x5000;

    /// <summary>3E 应答副头部（手册 p.42）。</summary>
    public const ushort Response3E = 0xD000;

    /// <summary>4E 请求副头部（手册 p.42）。</summary>
    public const ushort Request4E = 0x5400;

    /// <summary>4E 应答副头部（手册 p.42）。</summary>
    public const ushort Response4E = 0xD400;
}
