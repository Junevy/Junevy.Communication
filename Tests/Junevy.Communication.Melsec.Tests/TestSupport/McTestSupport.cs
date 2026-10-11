using System.Text;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Melsec.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// MELSEC 协议测试的共用辅助：手册字节表示的转换、按软元件表解析的请求数据、访问路径，以及帧的编码与解码入口。
/// 请求数据与帧都经由生产代码的内部入口编码，测试中不另写编码逻辑。
/// </summary>
internal static class McTestSupport
{
    /// <summary>手册示例的监视定时器 0x0010（250 ms 为单位，即 4 s；手册 p.43）。</summary>
    public const ushort MonitoringTimer = 0x0010;

    /// <summary>3E / 4E 的默认访问路径（计划 §18）：网络 00、PC FF、I/O 03FF、模块站 00。</summary>
    public static McAccessRoute DefaultEthernetRoute => new McAccessRoute(0x00, 0xFF, 0x03FF, 0x00);

    /// <summary>4C 的默认访问路径（计划 §18）：站号 00、网络号 00、PC FF、I/O 03FF、模块站 00、本站号 00。</summary>
    public static Mc4CAccessRoute DefaultSerialRoute => new Mc4CAccessRoute(0x00, 0x00, 0xFF, 0x03FF, 0x00, 0x00);

    /// <summary>把手册的二进制表示（空格分隔的十六进制，如 "50 00"）转换为字节。</summary>
    /// <param name="text">空格分隔的十六进制字节。</param>
    /// <returns>字节数组。</returns>
    public static byte[] Hex(string text)
    {
        string digits = text.Replace(" ", string.Empty);
        var bytes = new byte[digits.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(digits.Substring(i * 2, 2), 16);

        return bytes;
    }

    /// <summary>把手册的 ASCII 表示（连续字符，如 "5000"）转换为字节。</summary>
    /// <param name="text">ASCII 字符。</param>
    /// <returns>字节数组。</returns>
    public static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>按软元件表解析地址（例如 "TN100"；"X1A" 的编号为十六进制，即 26）。</summary>
    /// <param name="text">地址文本。</param>
    /// <returns>软元件与编号。</returns>
    public static (McDevice Device, int Number) Head(string text)
    {
        Assert.True(McDeviceTable.AddressParser.TryParse(text, out McAddress? address, out string? error), error);
        return (address!.Device, address.Number);
    }

    /// <summary>批量读（0401，字单位）的请求数据。</summary>
    public static byte[] WordRead(McEncoding encoding, string head, int points)
        => ReadBody(encoding, McCommands.WordUnit, head, points);

    /// <summary>批量读（0401，位单位）的请求数据。</summary>
    public static byte[] BitRead(McEncoding encoding, string head, int points)
        => ReadBody(encoding, McCommands.BitUnit, head, points);

    /// <summary>批量写（1401，字单位）的请求数据。</summary>
    /// <param name="littleEndianWords">规范化布局的字数据（每字 2 字节小端）。</param>
    public static byte[] WordWrite(McEncoding encoding, string head, byte[] littleEndianWords)
    {
        var (device, number) = Head(head);
        return McRequestBody.BatchWriteWords(McFieldCodec.For(encoding), device, number, littleEndianWords);
    }

    /// <summary>批量写（1401，位单位）的请求数据。</summary>
    public static byte[] BitWrite(McEncoding encoding, string head, bool[] points)
    {
        var (device, number) = Head(head);
        return McRequestBody.BatchWriteBits(McFieldCodec.For(encoding), device, number, points);
    }

    /// <summary>回环（0619）的请求数据。</summary>
    public static byte[] Loopback(McEncoding encoding, byte[] data)
        => McRequestBody.Loopback(McFieldCodec.For(encoding), data);

    /// <summary>用默认的 3E / 4E 访问路径与 <see cref="MonitoringTimer"/> 编码请求帧。</summary>
    public static byte[] Encode(McFrameType frameType, McEncoding encoding, ushort serial, byte[] body)
    {
        var output = new ByteBufferWriter();
        new McEthernetFrame(frameType, encoding).WriteRequest(output, serial, MonitoringTimer, DefaultEthernetRoute, body);
        return output.ToArray();
    }

    /// <summary>解码 3E / 4E 应答帧。</summary>
    public static CommResult<McResponse> Decode(McFrameType frameType, McEncoding encoding, ushort serial, byte[] frame,
        McDataKind kind = McDataKind.None, int count = 0)
        => new McEthernetFrame(frameType, encoding).ReadResponse(frame, serial, kind, count);

    /// <summary>断言解码成功并返回应答。</summary>
    public static McResponse Succeeded(CommResult<McResponse> response)
    {
        Assert.True(response.IsSuccess, response.ErrorMessage);
        return response.Data!;
    }

    /// <summary>断言解码结果为协议违规。</summary>
    public static void AssertProtocolViolation(CommResult<McResponse> response)
    {
        Assert.False(response.IsSuccess);
        Assert.Equal(CommErrorKind.ProtocolViolation, response.ErrorKind);
    }

    /// <summary>用 4C 帧编码请求（格式 4 为 ASCII，格式 5 为二进制）。</summary>
    public static byte[] Encode4C(McEncoding encoding, bool sumCheck, Mc4CAccessRoute route, byte[] body)
    {
        var output = new ByteBufferWriter();
        new Mc4CFrame(encoding, sumCheck).WriteRequest(output, route, body);
        return output.ToArray();
    }

    /// <summary>解码 4C 应答帧。</summary>
    public static CommResult<McResponse> Decode4C(McEncoding encoding, bool sumCheck, byte[] frame,
        McDataKind kind = McDataKind.None, int count = 0)
        => new Mc4CFrame(encoding, sumCheck).ReadResponse(frame, kind, count);

    /// <summary>按顺序连接字节片段（用于拼装含控制字节与 ASCII 字符的期望帧）。</summary>
    public static byte[] Join(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static byte[] ReadBody(McEncoding encoding, ushort subcommand, string head, int points)
    {
        var (device, number) = Head(head);
        return McRequestBody.BatchRead(McFieldCodec.For(encoding), subcommand, device, number, points);
    }
}
