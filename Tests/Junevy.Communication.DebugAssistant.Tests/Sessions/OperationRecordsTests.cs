using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.WebApi;

namespace Junevy.Communication.DebugAssistant.Tests.Sessions;

/// <summary>结果映射（计划 U6）：每种错误种类一行，显示的中文名称与库的消息都要保留。</summary>
public sealed class OperationRecordsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CommErrorKind.Unspecified, "未分类失败")]
    [InlineData(CommErrorKind.InvalidRequest, "请求无效")]
    [InlineData(CommErrorKind.ConnectionClosed, "连接已关闭")]
    [InlineData(CommErrorKind.Timeout, "超时")]
    [InlineData(CommErrorKind.ProtocolViolation, "协议违规")]
    [InlineData(CommErrorKind.RemoteError, "对端返回错误")]
    [InlineData(CommErrorKind.Cancelled, "已取消")]
    [InlineData(CommErrorKind.NotConnected, "未连接")]
    [InlineData(CommErrorKind.AuthenticationFailed, "认证失败")]
    [InlineData(CommErrorKind.ResourceExhausted, "资源耗尽")]
    [InlineData(CommErrorKind.NotSupported, "不支持")]
    public void CommFailure_KeepsItsKind_ChineseName_AndTheLibraryMessage(CommErrorKind kind, string expectedName)
    {
        OperationRecord record = OperationRecords.From(Now, "读取", "10.0.0.1:502", TimeSpan.FromMilliseconds(12), CommResult.Fail("the library message", kind));

        Assert.False(record.Succeeded);
        Assert.Equal(kind, record.ErrorKind);
        Assert.Equal(expectedName, record.ErrorKindText);
        Assert.Equal("the library message", record.Message);
        Assert.Null(record.ProtocolCode);
    }

    [Fact]
    public void CommSuccess_HasNoErrorKind()
    {
        OperationRecord record = OperationRecords.From(Now, "读取", "target", TimeSpan.Zero, CommResult.Success());

        Assert.True(record.Succeeded);
        Assert.Equal(CommErrorKind.None, record.ErrorKind);
        Assert.Equal("成功", record.ErrorKindText);
        Assert.Equal(string.Empty, record.Message);
    }

    [Fact]
    public void ProtocolErrorCode_IsShownInHex()
    {
        OperationRecord modbus = OperationRecords.From(Now, "读取", "t", TimeSpan.Zero, CommResult<int>.Fail("exception", CommErrorKind.RemoteError, 0x0B));
        OperationRecord mc = OperationRecords.From(Now, "读取", "t", TimeSpan.Zero, CommResult.Fail("end code", CommErrorKind.RemoteError, 0xC051));

        Assert.Equal("0x0B", modbus.ProtocolCode);
        Assert.Equal("0xC051", mc.ProtocolCode);
    }

    [Fact]
    public void WebApiFailure_ShowsTheRemoteCodeFirst_AndOtherwiseTheHttpStatus()
    {
        OperationRecord business = OperationRecords.From(Now, "POST", "/orders", TimeSpan.Zero,
            WebApiResult<string>.Fail("the order id must be positive", CommErrorKind.RemoteError, statusCode: 400, remoteCode: "E100"));
        OperationRecord http = OperationRecords.From(Now, "GET", "/missing", TimeSpan.Zero,
            WebApiResult<string>.Fail("not found", CommErrorKind.RemoteError, statusCode: 404));
        OperationRecord success = OperationRecords.From(Now, "GET", "/ok", TimeSpan.Zero, WebApiResult<string>.Success("ok", 200));

        Assert.Equal("E100", business.ProtocolCode);
        Assert.Equal("HTTP 404", http.ProtocolCode);
        Assert.Equal(CommErrorKind.RemoteError, business.ErrorKind);
        Assert.True(success.Succeeded);
        Assert.Equal("HTTP 200", success.ProtocolCode);
    }

    [Fact]
    public void EveryErrorKind_HasAChineseName()
    {
        foreach (CommErrorKind kind in Enum.GetValues<CommErrorKind>())
        {
            Assert.NotEqual(kind.ToString(), CommErrorKindNames.Of(kind));
        }
    }

    [Fact]
    public void DisplayTexts_ShowTheResultInChinese_AndTheElapsedTimeInMilliseconds()
    {
        var succeeded = new OperationRecord(Now, "读取", "HR0", TimeSpan.FromMilliseconds(12.4), true, CommErrorKind.None, null, string.Empty);
        var failed = succeeded with { Succeeded = false, Elapsed = TimeSpan.FromSeconds(1.5) };

        Assert.Equal(("成功", "12 ms"), (succeeded.ResultText, succeeded.ElapsedText));
        Assert.Equal(("失败", "1500 ms"), (failed.ResultText, failed.ElapsedText));
    }
}
