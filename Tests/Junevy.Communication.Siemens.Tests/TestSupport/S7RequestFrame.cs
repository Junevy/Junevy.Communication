namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// 请求帧的字段读取（计划第 17.1 节说明段）：TPKT 4 字节 + COTP DT 3 字节 + 请求头 10 字节，
/// 参数块从第 17 字节开始（服务号、项数、12 字节的地址项），写请求的数据块从第 31 字节开始、负载从第 35 字节开始。
/// 应答侧的位置记在 <see cref="S7TestSupport"/>；两侧合在一个文件里会超过 300 行，因此分开。
/// </summary>
internal static class S7RequestFrame
{
    // 请求布局的位置（V-S7-5、V-S7-7、V-S7-9 逐字节核对）。
    private const int ServiceIndex = 17;
    private const int TransportSizeIndex = 22;
    private const int QuantityHighIndex = 23;
    private const int DbNumberHighIndex = 25;
    private const int AreaCodeIndex = 27;
    private const int BitAddressHighIndex = 28;
    private const int DataTransportSizeIndex = 32;
    private const int PayloadIndex = 35;

    // 24 位地址字段是字节号 × 8 + 位号（17.1 节说明段）。
    private const int BitsPerByte = 8;

    /// <summary>服务号：读 0x04、写 0x05、Setup Communication 0xF0。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>服务号。</returns>
    public static byte Service(byte[] frame) => frame[ServiceIndex];

    /// <summary>地址项的传输尺寸（17.3 节：0x01 位、0x02 字节）。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>传输尺寸。</returns>
    public static byte TransportSize(byte[] frame) => frame[TransportSizeIndex];

    /// <summary>地址项的数量：字节访问按字节计、位访问按位计。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>数量。</returns>
    public static int Quantity(byte[] frame) => ReadUInt16(frame, QuantityHighIndex);

    /// <summary>DB 号；I、Q、M 区为 0。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>DB 号。</returns>
    public static int DbNumber(byte[] frame) => ReadUInt16(frame, DbNumberHighIndex);

    /// <summary>区域代码（17.3 节：I 0x81、Q 0x82、M 0x83、DB 0x84）。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>区域代码。</returns>
    public static byte AreaCode(byte[] frame) => frame[AreaCodeIndex];

    /// <summary>24 位的地址字段。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>地址字段的值（字节号 × 8 + 位号）。</returns>
    public static int BitAddress(byte[] frame)
        => frame[BitAddressHighIndex] << 16 | frame[BitAddressHighIndex + 1] << 8 | frame[BitAddressHighIndex + 2];

    /// <summary>地址字段里的字节号。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>字节号。</returns>
    public static int ByteAddress(byte[] frame) => BitAddress(frame) / BitsPerByte;

    /// <summary>地址字段里的位号。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>位号。</returns>
    public static int BitIndexOf(byte[] frame) => BitAddress(frame) % BitsPerByte;

    /// <summary>写请求数据项的传输尺寸（17.3 节：0x03 位、0x04 字节）。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>数据项传输尺寸。</returns>
    public static byte DataTransportSize(byte[] frame) => frame[DataTransportSizeIndex];

    /// <summary>写请求数据项之后的负载。</summary>
    /// <param name="frame">请求帧。</param>
    /// <returns>负载字节。</returns>
    public static byte[] Payload(byte[] frame) => frame.AsSpan(PayloadIndex).ToArray();

    private static int ReadUInt16(byte[] frame, int highIndex) => frame[highIndex] << 8 | frame[highIndex + 1];
}
