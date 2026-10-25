using Junevy.Communication.Core.Results;
using Xunit;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>断言通信结果成功。失败时断言消息包含错误种类、错误消息与协议错误码，便于从测试输出直接定位。</summary>
internal static class CommAssert
{
    /// <summary>断言无返回值的操作成功。</summary>
    public static void Succeeded(CommResult result) => Assert.True(result.IsSuccess, Describe(result));

    /// <summary>断言带返回值的操作成功。</summary>
    public static void Succeeded<T>(CommResult<T> result) => Assert.True(result.IsSuccess, Describe(result));

    private static string Describe(CommResult result) => $"{result.ErrorKind}: {result.ErrorMessage} (protocol code {FormatCode(result.ProtocolErrorCode)})";

    private static string Describe<T>(CommResult<T> result) => $"{result.ErrorKind}: {result.ErrorMessage} (protocol code {FormatCode(result.ProtocolErrorCode)})";

    private static string FormatCode(long? code) => code is { } value ? $"0x{value:X}" : "none";
}
