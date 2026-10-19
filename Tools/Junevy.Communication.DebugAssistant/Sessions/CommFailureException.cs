using Junevy.Communication.Core.Results;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>
/// 库返回失败结果时，页签的操作把它包成异常抛出，由基类按 U6 映射为 <see cref="OperationRecord"/>，
/// 保留库的错误种类、协议错误码与消息。
/// </summary>
public sealed class CommFailureException : Exception
{
    public CommFailureException(CommResult result)
        : base(result.ErrorMessage ?? result.ErrorKind.ToString())
    {
        Result = result;
    }

    public CommResult Result { get; }
}
