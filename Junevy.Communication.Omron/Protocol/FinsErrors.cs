using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Omron.Protocol;

/// <summary>
/// FINS 的结果工厂与错误映射（P4-D14）。消息文本固定：帧结构错误描述问题，设备侧错误只给出原始码，
/// 结束码按第 17.3 节的规则屏蔽后才判断。
/// </summary>
internal static class FinsErrors
{
    /// <summary>结束码与 FINS/TCP 错误码的 0 值：命令成功。</summary>
    public const byte NoError = 0x00;

    // 17.3 节：主码 & 0x7F、子码 & 0x3F，屏蔽后为 0 即成功（P4-D14、P4-D20 ⑤）。
    private const byte MajorMask = 0x7F;
    private const byte MinorMask = 0x3F;

    /// <summary>主码在高字节，原始码为 <c>major &lt;&lt; 8 | minor</c>。</summary>
    private const int MajorShift = 8;

    // CA2254：日志模板必须是常量。
    private const string FlaggedEndCodeTemplate = "FINS end code 0x{RawEndCode:X4} succeeded after masking the status flags.";

    /// <summary>帧结构错误（计划 Task 12 第 6 条）。</summary>
    /// <param name="message">英文描述。</param>
    /// <returns>失败结果。</returns>
    public static CommResult Violation(string message)
        => CommResult.Fail(message, CommErrorKind.ProtocolViolation);

    /// <summary>携带数据的帧结构错误。</summary>
    /// <typeparam name="T">数据类型。</typeparam>
    /// <param name="message">英文描述。</param>
    /// <returns>失败结果。</returns>
    public static CommResult<T> Violation<T>(string message)
        => CommResult<T>.Fail(message, CommErrorKind.ProtocolViolation);

    /// <summary>P4-D14：FINS/TCP 头的错误码非 0 是设备侧错误，原始码就是该码。</summary>
    /// <param name="error">外壳里的错误码。</param>
    /// <returns>为 0 时成功，否则为 <see cref="CommErrorKind.RemoteError"/>。</returns>
    public static CommResult CheckTcpError(uint error)
        => error == NoError
            ? CommResult.Success()
            : CommResult.Fail($"FINS/TCP error 0x{error:X8}.", CommErrorKind.RemoteError, error);

    /// <summary>
    /// P4-D14：结束码先屏蔽状态位再判断。屏蔽后为 0 是成功，原码含标志位时记一条 Debug；
    /// 仍非 0 时是设备侧错误，原始码取屏蔽后的码，消息格式固定。
    /// </summary>
    /// <param name="major">结束码主码。</param>
    /// <param name="minor">结束码子码。</param>
    /// <param name="logger">记录非致命标志位的日志器；不需要日志时为 null。</param>
    /// <returns>成功或 <see cref="CommErrorKind.RemoteError"/>。</returns>
    public static CommResult CheckEndCode(byte major, byte minor, ILogger? logger)
    {
        ushort masked = (ushort)((major & MajorMask) << MajorShift | minor & MinorMask);
        if (masked == NoError)
        {
            if (major != NoError || minor != NoError)
            {
                logger?.LogDebug(FlaggedEndCodeTemplate, (ushort)(major << MajorShift | minor));
            }

            return CommResult.Success();
        }

        return CommResult.Fail($"FINS end code 0x{masked:X4}.", CommErrorKind.RemoteError, masked);
    }
}
