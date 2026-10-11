using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Siemens.Protocol;

/// <summary>
/// S7 的结果工厂与错误映射（P4-D14）。消息文本固定，返回码与错误类的名称只取自计划第 17.3 节的码表，
/// 表外的码不带名称。
/// </summary>
internal static class S7Errors
{
    /// <summary>错误类与错误码的 0 值：17.3 节的 "0x00 no error"。</summary>
    public const byte NoError = 0x00;

    /// <summary>错误类的位移：原始码按 P4-D14 组合为 <c>类 &lt;&lt; 8 | 码</c>。</summary>
    private const int ErrorClassShift = 8;

    // P4-D14 指定这三个返回码要追加 PUT/GET 提示；名称取自 17.3 节。
    private const byte ItemAccessNotAllowed = 0x03;
    private const byte ItemInvalidAddress = 0x05;
    private const byte ItemDoesNotExist = 0x0A;

    // P4-D14 指定错误类 0x81、错误码 0x04 的组合也追加同一提示。
    private const ushort PutGetError = 0x8104;

    private const string PutGetHint = " Check that PUT/GET access is permitted in the CPU protection settings and that the data block does not use optimized block access.";

    /// <summary>帧结构错误（计划 Task 7 第 4 条）。</summary>
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

    /// <summary>
    /// 检查应答头的错误类与错误码（P4-D14）：任一非 0 都是设备侧错误，原始码为 <c>类 &lt;&lt; 8 | 码</c>，
    /// 组合值 0x8104 追加 PUT/GET 提示。
    /// </summary>
    /// <param name="errorClass">应答头错误类。</param>
    /// <param name="errorCode">应答头错误码。</param>
    /// <returns>两个字节都为 0 时成功，否则为 <see cref="CommErrorKind.RemoteError"/>。</returns>
    public static CommResult CheckHeader(byte errorClass, byte errorCode)
    {
        if (errorClass == NoError && errorCode == NoError)
            return CommResult.Success();

        ushort combined = (ushort)(errorClass << ErrorClassShift | errorCode);
        string message = $"S7 error class 0x{errorClass:X2} code 0x{errorCode:X2}.";
        if (combined == PutGetError)
            message += PutGetHint;

        return CommResult.Fail(message, CommErrorKind.RemoteError, combined);
    }

    /// <summary>数据项返回码非 0xFF（P4-D14）：原始码就是返回码本身。</summary>
    /// <param name="returnCode">数据项返回码。</param>
    /// <returns>失败结果。</returns>
    public static CommResult ItemFailure(byte returnCode)
        => CommResult.Fail(ItemMessage(returnCode), CommErrorKind.RemoteError, returnCode);

    /// <summary>携带数据的数据项失败。</summary>
    /// <typeparam name="T">数据类型。</typeparam>
    /// <param name="returnCode">数据项返回码。</param>
    /// <returns>失败结果。</returns>
    public static CommResult<T> ItemFailure<T>(byte returnCode)
        => ItemFailure(returnCode).As<T>();

    private static string ItemMessage(byte returnCode)
    {
        string? name = ReturnCodeName(returnCode);
        string message = name is null
            ? $"S7 item return code 0x{returnCode:X2}."
            : $"S7 item return code 0x{returnCode:X2} ({name}).";

        // 17.3 节里这三个码都指向 PUT/GET 权限或优化块访问。
        return returnCode is ItemAccessNotAllowed or ItemInvalidAddress or ItemDoesNotExist
            ? message + PutGetHint
            : message;
    }

    private static string? ReturnCodeName(byte returnCode) => returnCode switch
    {
        0x01 => "hardware fault",
        ItemAccessNotAllowed => "accessing the object not allowed",
        ItemInvalidAddress => "invalid address",
        0x06 => "data type not supported",
        0x07 => "data type inconsistent",
        ItemDoesNotExist => "object does not exist",
        _ => null,
    };
}
