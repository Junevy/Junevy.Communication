namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// 串口 4C 帧的访问路径（手册 p.45）：站号、网络号、PC 号各 1 字节；请求目标模块 I/O 号 2 字节；请求目标模块站号与本站号各 1 字节。
/// </summary>
internal readonly struct Mc4CAccessRoute
{
    /// <summary>创建访问路径。</summary>
    /// <param name="stationNumber">站号。</param>
    /// <param name="networkNumber">网络号。</param>
    /// <param name="pcNumber">PC 号。</param>
    /// <param name="moduleIoNumber">请求目标模块 I/O 号。</param>
    /// <param name="moduleStationNumber">请求目标模块站号。</param>
    /// <param name="selfStationNumber">本站号。</param>
    public Mc4CAccessRoute(byte stationNumber, byte networkNumber, byte pcNumber, ushort moduleIoNumber, byte moduleStationNumber, byte selfStationNumber)
    {
        StationNumber = stationNumber;
        NetworkNumber = networkNumber;
        PcNumber = pcNumber;
        ModuleIoNumber = moduleIoNumber;
        ModuleStationNumber = moduleStationNumber;
        SelfStationNumber = selfStationNumber;
    }

    /// <summary>站号。</summary>
    public byte StationNumber { get; }

    /// <summary>网络号。</summary>
    public byte NetworkNumber { get; }

    /// <summary>PC 号。</summary>
    public byte PcNumber { get; }

    /// <summary>请求目标模块 I/O 号。</summary>
    public ushort ModuleIoNumber { get; }

    /// <summary>请求目标模块站号。</summary>
    public byte ModuleStationNumber { get; }

    /// <summary>本站号。</summary>
    public byte SelfStationNumber { get; }

    /// <summary>访问路径的字段宽度：二进制 7 字节，ASCII 14 个字符。</summary>
    /// <param name="codec">字段编码策略。</param>
    /// <returns>字段宽度。</returns>
    public static int FieldSize(McFieldCodec codec) => 5 * codec.UInt8Size + codec.UInt16Size;

    /// <summary>写入访问路径。</summary>
    /// <param name="codec">字段编码策略。</param>
    /// <param name="output">输出。</param>
    public void Write(McFieldCodec codec, Stream output)
    {
        codec.WriteUInt8(output, StationNumber);
        codec.WriteUInt8(output, NetworkNumber);
        codec.WriteUInt8(output, PcNumber);
        codec.WriteUInt16(output, ModuleIoNumber);
        codec.WriteUInt8(output, ModuleStationNumber);
        codec.WriteUInt8(output, SelfStationNumber);
    }
}
