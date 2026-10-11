using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// S7 协议层测试的共用内容：计划第 17.1 节的向量、由这些向量派生的帧，以及 <see cref="CommResult"/> 的断言辅助。
/// 派生向量都注明派生来源与改动点，便于与 17.1 节逐字节对照。
/// </summary>
internal static class S7TestSupport
{
    /// <summary>P4-D19 的连接类型 PG。</summary>
    public const int Pg = 1;

    /// <summary>P4-D19 的连接类型 OP。</summary>
    public const int Op = 2;

    /// <summary>P4-D19 的连接类型 Basic。</summary>
    public const int Basic = 3;

    /// <summary>P4-D19 的默认 Rack。</summary>
    public const int Rack = 0;

    /// <summary>P4-D19 的默认槽号。</summary>
    public const int Slot = 1;

    /// <summary>PUT/GET 未开启时 PLC 的常见提示（P4-D14 固定文本）。</summary>
    public const string PutGetHint = "Check that PUT/GET access is permitted in the CPU protection settings and that the data block does not use optimized block access.";

    // ———— 第 17.1 节的向量 ————

    /// <summary>V-S7-1：COTP CR，Rack 0、Slot 1、PG。</summary>
    public const string V_S7_1 = "03 00 00 16 11 E0 00 00 00 01 00 C0 01 0A C1 02 01 00 C2 02 01 01";

    /// <summary>V-S7-2：COTP CC，只校验 PDU 类型 D0。</summary>
    public const string V_S7_2 = "03 00 00 16 11 D0 00 01 00 0C 00 C0 01 0A C1 02 01 00 C2 02 01 01";

    /// <summary>V-S7-3：Setup Communication，引用号 0x0001，请求 960。</summary>
    public const string V_S7_3 = "03 00 00 19 02 F0 80 32 01 00 00 00 01 00 08 00 00 F0 00 00 01 00 01 03 C0";

    /// <summary>V-S7-4：Setup 应答，协商 240。</summary>
    public const string V_S7_4 = "03 00 00 1B 02 F0 80 32 03 00 00 00 01 00 08 00 00 00 00 F0 00 00 01 00 01 00 F0";

    /// <summary>V-S7-5：读 DB1.DBD0 的 4 字节，引用号 0x0002。</summary>
    public const string V_S7_5 = "03 00 00 1F 02 F0 80 32 01 00 00 00 02 00 0E 00 00 04 01 12 0A 10 02 00 04 00 01 84 00 00 00";

    /// <summary>V-S7-6：V-S7-5 的应答，数据 4 字节。</summary>
    public const string V_S7_6 = "03 00 00 1D 02 F0 80 32 03 00 00 00 02 00 02 00 08 00 00 04 01 FF 04 00 20 41 20 00 00";

    /// <summary>V-S7-7：写 DB1.DBW2 = 0x1234，引用号 0x0003。</summary>
    public const string V_S7_7 = "03 00 00 25 02 F0 80 32 01 00 00 00 03 00 0E 00 06 05 01 12 0A 10 02 00 02 00 01 84 00 00 10 00 04 00 10 12 34";

    /// <summary>V-S7-8：V-S7-7 的应答，数据只有一个返回码。</summary>
    public const string V_S7_8 = "03 00 00 16 02 F0 80 32 03 00 00 00 03 00 02 00 01 00 00 05 01 FF";

    /// <summary>V-S7-9：写 DB1.DBX10.3 = 1，引用号 0x0004。</summary>
    public const string V_S7_9 = "03 00 00 24 02 F0 80 32 01 00 00 00 04 00 0E 00 05 05 01 12 0A 10 01 00 01 00 01 84 00 00 53 00 03 00 01 01";

    /// <summary>V-S7-10：读 MB10 的 1 字节，引用号 0x0005。</summary>
    public const string V_S7_10 = "03 00 00 1F 02 F0 80 32 01 00 00 00 05 00 0E 00 00 04 01 12 0A 10 02 00 01 00 00 83 00 00 50";

    /// <summary>V-S7-10 的应答，数据 1 字节。</summary>
    public const string V_S7_10_Ack = "03 00 00 1A 02 F0 80 32 03 00 00 00 05 00 02 00 05 00 00 04 01 FF 04 00 08 5A";

    /// <summary>V-S7-11：数据项返回码 0x0A，引用号 0x0006。</summary>
    public const string V_S7_11 = "03 00 00 19 02 F0 80 32 03 00 00 00 06 00 02 00 04 00 00 04 01 0A 00 00 00";

    /// <summary>V-S7-12：应答头错误类 0x81、错误码 0x04，引用号 0x0007。</summary>
    public const string V_S7_12 = "03 00 00 13 02 F0 80 32 03 00 00 00 07 00 00 00 00 81 04";

    // ———— 由 17.1 节向量派生的帧 ————

    /// <summary>V-S7-1 派生：连接类型改为 OP（远端 TSAP 0x0201，P4-D19）。</summary>
    public const string Cr_Op = "03 00 00 16 11 E0 00 00 00 01 00 C0 01 0A C1 02 01 00 C2 02 02 01";

    /// <summary>V-S7-1 派生：连接类型 Basic、Rack 1、槽 2（远端 TSAP 0x0322，P4-D19）。</summary>
    public const string Cr_Basic_Rack1_Slot2 = "03 00 00 16 11 E0 00 00 00 01 00 C0 01 0A C1 02 01 00 C2 02 03 22";

    /// <summary>V-S7-4 派生：协商值改为 960。</summary>
    public const string Setup_Ack_960 = "03 00 00 1B 02 F0 80 32 03 00 00 00 01 00 08 00 00 00 00 F0 00 00 01 00 01 03 C0";

    /// <summary>V-S7-4 派生：参数只有 4 字节。</summary>
    public const string Setup_Ack_ShortParameters = "03 00 00 17 02 F0 80 32 03 00 00 00 01 00 04 00 00 00 00 F0 00 00 01";

    /// <summary>V-S7-4 派生：参数号改为 0x04（不是 0xF0）。</summary>
    public const string Setup_Ack_OtherParameter = "03 00 00 1B 02 F0 80 32 03 00 00 00 01 00 08 00 00 00 00 04 00 00 01 00 01 00 F0";

    /// <summary>V-S7-7 派生：写 4 字节（两字），传输尺寸仍为 0x04、长度按位计（计划第 11 节第 1 条）。</summary>
    public const string Write_2Words = "03 00 00 27 02 F0 80 32 01 00 00 00 03 00 0E 00 08 05 01 12 0A 10 02 00 04 00 01 84 00 00 10 00 04 00 20 12 34 56 78";

    /// <summary>V-S7-9 派生：写入的位为 0。</summary>
    public const string WriteBit_Zero = "03 00 00 24 02 F0 80 32 01 00 00 00 04 00 0E 00 05 05 01 12 0A 10 01 00 01 00 01 84 00 00 53 00 03 00 01 00";

    /// <summary>V-S7-6 派生：数据项传输尺寸 0x09（长度按字节，17.3 节）。</summary>
    public const string Read_Ack_OctetString = "03 00 00 1D 02 F0 80 32 03 00 00 00 02 00 02 00 08 00 00 04 01 FF 09 00 04 41 20 00 00";

    /// <summary>V-S7-6 派生：数据项传输尺寸 0x03，长度 32 位（17.3 节按位计）。</summary>
    public const string Read_Ack_32Bits = "03 00 00 1D 02 F0 80 32 03 00 00 00 02 00 02 00 08 00 00 04 01 FF 03 00 20 41 20 00 00";

    /// <summary>V-S7-10 应答派生：数据项传输尺寸 0x03，长度 3 位（向上取整为 1 字节）。</summary>
    public const string Read_Ack_3Bits = "03 00 00 1A 02 F0 80 32 03 00 00 00 05 00 02 00 05 00 00 04 01 FF 03 00 03 5A";

    /// <summary>V-S7-6 派生：参数为 4 字节（含多余的项）。</summary>
    public const string Read_Ack_LongParameters = "03 00 00 1F 02 F0 80 32 03 00 00 00 02 00 04 00 08 00 00 04 01 12 0A FF 04 00 20 41 20 00 00";

    /// <summary>V-S7-6 派生：数据长度字段说 4 字节，实际只有 2 字节。</summary>
    public const string Read_Ack_ShortPayload = "03 00 00 1B 02 F0 80 32 03 00 00 00 05 00 02 00 06 00 00 04 01 FF 04 00 20 41 20";

    /// <summary>V-S7-6 派生：数据项之后还有多余的字节。</summary>
    public const string Read_Ack_TrailingBytes = "03 00 00 1E 02 F0 80 32 03 00 00 00 02 00 02 00 09 00 00 04 01 FF 04 00 20 41 20 00 00 AA";

    /// <summary>V-S7-8 派生：数据长度为 0，没有返回码。</summary>
    public const string Write_Ack_WithoutReturnCode = "03 00 00 15 02 F0 80 32 03 00 00 00 03 00 02 00 00 00 00 05 01";

    /// <summary>V-S7-2 派生：PDU 类型改为 CR（0xE0），不是应答。</summary>
    public const string Cc_WrongPduType = "03 00 00 16 11 E0 00 01 00 0C 00 C0 01 0A C1 02 01 00 C2 02 01 01";

    /// <summary>V-S7-8 派生：返回码之后还有一个填充字节。</summary>
    public const string Write_Ack_Padded = "03 00 00 17 02 F0 80 32 03 00 00 00 03 00 02 00 02 00 00 05 01 FF 00";

    // ———— 应答帧中的字节位置：TPKT 4 字节 + COTP DT 3 字节 + 应答头 12 字节 ————

    /// <summary>TPKT 版本号的位置。</summary>
    public const int TpktVersion = 0;

    /// <summary>TPKT 长度字段低位字节的位置。</summary>
    public const int TpktLengthLow = 3;

    /// <summary>COTP 长度字节的位置。</summary>
    public const int CotpLength = 4;

    /// <summary>COTP PDU 类型的位置。</summary>
    public const int CotpPduType = 5;

    /// <summary>S7 协议号（0x32）的位置。</summary>
    public const int ProtocolId = 7;

    /// <summary>ROSCTR 的位置。</summary>
    public const int Rosctr = 8;

    /// <summary>应答头参数长度低位字节的位置。</summary>
    public const int ParameterLengthLow = 14;

    /// <summary>应答头数据长度低位字节的位置。</summary>
    public const int DataLengthLow = 16;

    /// <summary>应答头错误类的位置。</summary>
    public const int ErrorClass = 17;

    /// <summary>应答头错误码的位置。</summary>
    public const int ErrorCode = 18;

    /// <summary>应答参数块的服务号位置（读为 0x04、写为 0x05）。</summary>
    public const int ResponseFunction = 19;

    /// <summary>应答参数块中的数据项个数位置。</summary>
    public const int ItemCount = 20;

    /// <summary>数据项返回码的位置。</summary>
    public const int ItemReturnCode = 21;

    /// <summary>数据项传输尺寸的位置。</summary>
    public const int TransportSize = 22;

    /// <summary>数据项长度低位字节的位置。</summary>
    public const int UnitLengthLow = 24;

    /// <summary>Setup 应答中协商 PDU 长度低位字节的位置（参数为 8 字节）。</summary>
    public const int NegotiatedPduLow = 26;

    /// <summary>V-S7-2 的最后一个字节：远端 TSAP 参数的低位，属于 CC 不解析的参数区。</summary>
    public const int ConfirmParameterTail = 21;

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

    /// <summary>断言帧结构错误（计划 Task 7 第 4 条）。</summary>
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
