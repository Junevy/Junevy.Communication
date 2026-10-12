using Junevy.Communication.Core.Results;
using Junevy.Communication.Omron.Protocol;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// FINS 协议层测试的共用内容：计划第 17.2 节的九条向量、向量里的字段位置，以及 <see cref="CommResult"/> 的断言辅助。
/// 派生向量都注明派生来源与改动点，便于与 17.2 节逐字节对照。
/// </summary>
internal static class FinsTestSupport
{
    /// <summary>17.2 节说明段：FINS/TCP 头为 "FINS" 4 + 长度 4 + 命令 4 + 错误码 4。</summary>
    public const int ShellBytes = 16;

    /// <summary>长度字段的起点。</summary>
    public const int TcpLengthIndex = 4;

    /// <summary>FINS/TCP 命令字段的起点（0 节点地址请求、1 节点地址应答、2 FINS 帧）。</summary>
    public const int TcpCommandIndex = 8;

    /// <summary>FINS/TCP 错误码字段的起点。</summary>
    public const int TcpErrorIndex = 12;

    /// <summary>魔数 "FINS" 的末字节位置。</summary>
    public const int MagicTailIndex = 3;

    /// <summary>FINS 头的 ICF 字节位置（应答必须带 0x40）。</summary>
    public const int IcfIndex = ShellBytes;

    /// <summary>17.2 节说明段：FINS 头的 SID 是第 10 个字节。</summary>
    public const int SidIndex = ShellBytes + 9;

    /// <summary>命令码（回显）的高字节位置。</summary>
    public const int CommandIndex = ShellBytes + 10;

    /// <summary>结束码主码位置（命令码之后 2 字节）。</summary>
    public const int EndCodeMajorIndex = ShellBytes + 12;

    /// <summary>结束码子码位置。</summary>
    public const int EndCodeMinorIndex = ShellBytes + 13;

    /// <summary>内存区读写的区域代码位置（FINS 参数首字节）。</summary>
    public const int AreaCodeIndex = ShellBytes + 12;

    /// <summary>节点地址体（请求与应答都是大端 4 字节）里客户端节点字段的末字节位置。</summary>
    public const int ClientNodeTailIndex = 19;

    /// <summary>节点地址应答里服务端节点字段的末字节位置（应答体为客户端 4 字节 + 服务端 4 字节）。</summary>
    public const int ServerNodeTailIndex = 23;

    /// <summary>P4-D18 与 17.2 节说明段：向量里的客户端节点 0x0B、服务端节点 0x01。</summary>
    public const byte ClientNode = 0x0B;

    /// <summary>向量里的服务端节点号。</summary>
    public const byte ServerNode = 0x01;

    /// <summary>请求的路由：目的＝服务端节点，源＝客户端节点；网络号与单元号按 P4-D18 固定为 0。</summary>
    public static readonly FinsRoute Route = new FinsRoute(0, ServerNode, 0, 0, ClientNode, 0);

    /// <summary>P4-D14 固定消息的前缀格式（结束码）。</summary>
    public static string EndCodeMessage(ushort code) => $"FINS end code 0x{code:X4}.";

    /// <summary>P4-D14：FINS/TCP 头的错误码非 0 时 RemoteError 的消息格式。</summary>
    public static string TcpErrorMessage(uint error) => $"FINS/TCP error 0x{error:X8}.";

    /// <summary>改写应答的结束码两字节（17.3 节屏蔽规则的变异检查用）。</summary>
    public static byte[] WithEndCode(string vector, int major, int minor)
    {
        byte[] frame = ToBytes(vector);
        frame[EndCodeMajorIndex] = (byte)major;
        frame[EndCodeMinorIndex] = (byte)minor;
        return frame;
    }

    // ———— 第 17.2 节的向量 ————

    /// <summary>V-F-1：节点地址请求，客户端节点 0 表示自动分配。</summary>
    public const string V_F_1 = "46 49 4E 53 00 00 00 0C 00 00 00 00 00 00 00 00 00 00 00 00";

    /// <summary>V-F-2：节点地址应答，客户端 0x0B、服务端 0x01。</summary>
    public const string V_F_2 = "46 49 4E 53 00 00 00 10 00 00 00 01 00 00 00 00 00 00 00 0B 00 00 00 01";

    /// <summary>V-F-3：读 D100 的 2 字，SID 0x01。</summary>
    public const string V_F_3 = "46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 01 01 01 82 00 64 00 00 02";

    /// <summary>V-F-4：V-F-3 的应答，数据 12 34 56 78。</summary>
    public const string V_F_4 = "46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 C0 00 02 00 0B 00 00 01 00 01 01 01 00 00 12 34 56 78";

    /// <summary>V-F-5：写 D200 = 0x00FF，SID 0x02。</summary>
    public const string V_F_5_Request = "46 49 4E 53 00 00 00 1C 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 02 01 02 82 00 C8 00 00 01 00 FF";

    /// <summary>V-F-5 的应答：结束码之后没有数据。</summary>
    public const string V_F_5_Response = "46 49 4E 53 00 00 00 16 00 00 00 02 00 00 00 00 C0 00 02 00 0B 00 00 01 00 02 01 02 00 00";

    /// <summary>V-F-6：读位 CIO0.00 起 3 点，SID 0x03。</summary>
    public const string V_F_6_Request = "46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 03 01 01 30 00 00 00 00 03";

    /// <summary>V-F-6 的应答：每点 1 字节，取值 0x00 或 0x01。</summary>
    public const string V_F_6_Response = "46 49 4E 53 00 00 00 19 00 00 00 02 00 00 00 00 C0 00 02 00 0B 00 00 01 00 03 01 01 00 00 01 00 01";

    /// <summary>V-F-7：写位 W10.03 = 1，SID 0x04。</summary>
    public const string V_F_7 = "46 49 4E 53 00 00 00 1B 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 04 01 02 31 00 0A 03 00 01 01";

    /// <summary>V-F-8：读取失败，结束码 0x1103，SID 0x05。</summary>
    public const string V_F_8 = "46 49 4E 53 00 00 00 16 00 00 00 02 00 00 00 00 C0 00 02 00 0B 00 00 01 00 05 01 01 11 03";

    /// <summary>V-F-9：成功但带非致命错误标志（结束码 00 40），数据 00 2A，SID 0x06。</summary>
    public const string V_F_9 = "46 49 4E 53 00 00 00 18 00 00 00 02 00 00 00 00 C0 00 02 00 0B 00 00 01 00 06 01 01 00 40 00 2A";

    // ———— 由 17.2 节向量派生的用例（改动点在名字与注释里写明） ————

    /// <summary>由 V-F-5 派生：写 D200 起 2 字，数据 00 FF 12 34（长度 0x1E = 36 - 8）。</summary>
    public const string Write_2Words = "46 49 4E 53 00 00 00 1E 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 02 01 02 82 00 C8 00 00 02 00 FF 12 34";

    /// <summary>由 V-F-7 派生：写 W10.03 起 3 点 1/0/1，SID 0x04、长度 0x1D。</summary>
    public const string WriteBits_3Points = "46 49 4E 53 00 00 00 1D 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 04 01 02 31 00 0A 03 00 03 01 00 01";

    /// <summary>由 V-F-7 派生：唯一的点写 0（数据字节 00）。</summary>
    public const string WriteBit_Zero = "46 49 4E 53 00 00 00 1B 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 0B 00 04 01 02 31 00 0A 03 00 01 00";

    /// <summary>由 V-F-5 的应答派生：写应答末尾多出 2 字节数据，长度字段同步改成 0x18。</summary>
    public const string Write_Ack_UnexpectedData = "46 49 4E 53 00 00 00 18 00 00 00 02 00 00 00 00 C0 00 02 00 0B 00 00 01 00 02 01 02 00 00 00 FF";

    /// <summary>由 V-F-1 派生：请求固定客户端节点号 0x0B（P4-D18、P4-D20 ⑥）。</summary>
    public const string NodeRequest_FixedClientNode = "46 49 4E 53 00 00 00 0C 00 00 00 00 00 00 00 00 00 00 00 0B";

    // ———— 17.3 节的区域代码（字 / 位）在向量里用到的四项 ————

    /// <summary>D 区字代码。</summary>
    public const byte DWordArea = 0x82;

    /// <summary>W 区位代码。</summary>
    public const byte WBitArea = 0x31;

    /// <summary>CIO 区位代码。</summary>
    public const byte CioBitArea = 0x30;

    /// <summary>把向量改写成"应答头错误码非 0"的形态（P4-D14 最后一条）。</summary>
    /// <param name="vector">原始向量。</param>
    /// <param name="error">要写入的 FINS/TCP 错误码。</param>
    /// <returns>改写后的帧字节。</returns>
    public static byte[] WithTcpError(string vector, uint error)
    {
        byte[] frame = ToBytes(vector);
        frame[TcpErrorIndex] = (byte)(error >> 24);
        frame[TcpErrorIndex + 1] = (byte)(error >> 16);
        frame[TcpErrorIndex + 2] = (byte)(error >> 8);
        frame[TcpErrorIndex + 3] = (byte)error;
        return frame;
    }

    /// <summary>在向量末尾追加若干字节，用于"长度字段与帧长不符"的用例。</summary>
    /// <param name="vector">原始向量。</param>
    /// <param name="extra">追加的字节。</param>
    /// <returns>追加后的帧字节。</returns>
    public static byte[] Appended(string vector, string extra) => Concat(vector, extra);

    /// <summary>拼接两段十六进制。</summary>
    /// <param name="first">前段。</param>
    /// <param name="second">后段。</param>
    /// <returns>拼接后的字节。</returns>
    public static byte[] Concat(string first, string second)
    {
        byte[] a = ToBytes(first);
        byte[] b = ToBytes(second);
        byte[] joined = new byte[a.Length + b.Length];
        a.CopyTo(joined, 0);
        b.CopyTo(joined, a.Length);
        return joined;
    }

    /// <summary>解析地址文本；失败即断言失败。钩子要用的实参（区域代码、字号、位号）都取自解析后的地址。</summary>
    /// <param name="text">地址文本。</param>
    /// <returns>解析出的地址。</returns>
    public static FinsAddress Address(string text)
    {
        Assert.True(FinsDeviceTable.AddressParser.TryParse(text, out FinsAddress? address, out string? error), error);
        return address!;
    }

    // ———— 断言辅助 ————

    /// <summary>断言结果成功并返回其数据。</summary>
    /// <typeparam name="T">数据类型。</typeparam>
    /// <param name="result">结果。</param>
    /// <returns>成功时的数据。</returns>
    public static T Succeeded<T>(CommResult<T> result)
    {
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return result.Data!;
    }

    /// <summary>断言成功结果（不携带数据）。</summary>
    /// <param name="result">结果。</param>
    public static void Succeeded(CommResult result)
        => Assert.True(result.IsSuccess, result.ErrorMessage);

    /// <summary>断言帧结构错误（计划 Task 12 第 6 条）。</summary>
    /// <param name="result">结果。</param>
    public static void AssertViolation(CommResult result)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    /// <summary>断言携带数据的帧结构错误。</summary>
    /// <typeparam name="T">数据类型。</typeparam>
    /// <param name="result">结果。</param>
    public static void AssertViolation<T>(CommResult<T> result) => AssertViolation(result.ToResult());

    /// <summary>断言设备侧错误（P4-D14）：分类、原始码与固定消息。</summary>
    /// <param name="result">结果。</param>
    /// <param name="protocolErrorCode">协议原始码。</param>
    /// <param name="message">固定消息文本。</param>
    public static void AssertRemoteError(CommResult result, long protocolErrorCode, string message)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(protocolErrorCode, result.ProtocolErrorCode);
        Assert.Equal(message, result.ErrorMessage);
    }

    /// <summary>断言携带数据的设备侧错误。</summary>
    /// <typeparam name="T">数据类型。</typeparam>
    /// <param name="result">结果。</param>
    /// <param name="protocolErrorCode">协议原始码。</param>
    /// <param name="message">固定消息文本。</param>
    public static void AssertRemoteError<T>(CommResult<T> result, long protocolErrorCode, string message)
        => AssertRemoteError(result.ToResult(), protocolErrorCode, message);
}
