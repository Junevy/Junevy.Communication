using System.Diagnostics;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Ftp;

/// <summary>
/// 一次操作的日志信息（P63-D15）：操作名、目标（远程路径）、传输的字节数与耗时。
/// 门面创建它，传输操作填入字节数；生命周期在拿到串行门之后开始计时，结束时记录一条日志。
/// 日志只含操作名、路径、字节数、耗时与失败分类，不含文件内容与凭据。
/// </summary>
internal sealed class FtpOperation
{
    private const string SucceededTemplate = "FTP {Operation} {Target} succeeded in {ElapsedMs} ms.";
    private const string TransferredTemplate = "FTP {Operation} {Target} succeeded in {ElapsedMs} ms, {Bytes} bytes.";
    private const string CancelledTemplate = "FTP {Operation} {Target} was cancelled after {ElapsedMs} ms.";
    private const string FailedTemplate = "FTP {Operation} {Target} failed after {ElapsedMs} ms: {ErrorKind} (reply {ReplyCode}) {Error}";

    private readonly Stopwatch timer = new();

    /// <summary>创建操作信息。</summary>
    /// <param name="name">操作名，例如 <c>List</c>、<c>UploadFile</c>。</param>
    /// <param name="target">操作的目标，通常是远程路径。</param>
    public FtpOperation(string name, string target)
    {
        Name = name;
        Target = target;
    }

    /// <summary>操作名。</summary>
    public string Name { get; }

    /// <summary>操作的目标（远程路径）。</summary>
    public string Target { get; }

    /// <summary>传输的字节数；不是传输操作时为 null。</summary>
    public long? TransferredBytes { get; set; }

    /// <summary>开始计时（拿到串行门之后），排队等待的时间不计入耗时。</summary>
    public void Start() => timer.Start();

    /// <summary>记录操作的结果：成功记 Debug，调用方取消记 Debug，其余失败记 Warning。</summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="result">操作的结果。</param>
    public void LogCompletion(ILogger logger, CommResult result)
    {
        long elapsed = timer.ElapsedMilliseconds;
        if (result.IsSuccess)
        {
            if (TransferredBytes is { } bytes)
            {
                logger.LogDebug(TransferredTemplate, Name, Target, elapsed, bytes);
            }
            else
            {
                logger.LogDebug(SucceededTemplate, Name, Target, elapsed);
            }
        }
        else if (result.ErrorKind == CommErrorKind.Cancelled)
        {
            logger.LogDebug(CancelledTemplate, Name, Target, elapsed);
        }
        else
        {
            logger.LogWarning(FailedTemplate, Name, Target, elapsed, result.ErrorKind, result.ProtocolErrorCode, result.ErrorMessage);
        }
    }
}
