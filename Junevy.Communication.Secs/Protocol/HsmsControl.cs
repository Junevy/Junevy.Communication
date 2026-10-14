namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// HSMS 控制消息的构造（P51-D6、D11、D12）。
/// </summary>
/// <remarks>
/// <strong>System Bytes 一律由调用方显式传入</strong>，本类不偷偷分配：应答必须原样回送对方消息的号，
/// 而我方发起的请求才用 <see cref="HsmsSystemBytes.Next"/>。写成参数以后，
/// "这条报文用的是谁的号"在每个调用点都看得见，黄金向量也能逐字节比对。
/// </remarks>
internal static class HsmsControl
{
    /// <summary>Select 成功：状态码 0（SEMI E37 的 "communication established"）。</summary>
    internal const byte SelectOk = 0;

    /// <summary>Select 的状态码 1：会话已激活（P51-D12，收到重复的 <c>Select.req</c> 时回它）。</summary>
    internal const byte SelectAlreadyActive = 1;

    /// <summary><c>Select.req</c>：发起选择；HSMS-SS 里只有 Active 一方发起（P51-D11）。</summary>
    internal static HsmsMessage SelectRequest(uint systemBytes)
        => HsmsMessage.Control(HsmsMessage.AnySession, 0, 0, HsmsControlKind.SelectRequest, systemBytes);

    /// <summary><c>Select.rsp</c>：应答选择，头字节 3 是状态码。</summary>
    internal static HsmsMessage SelectResponse(uint systemBytes, byte status)
        => HsmsMessage.Control(HsmsMessage.AnySession, 0, status, HsmsControlKind.SelectResponse, systemBytes);

    /// <summary><c>Deselect.req</c>：HSMS-SS 不用它，本库收到会拒绝（P51-D12）。</summary>
    internal static HsmsMessage DeselectRequest(uint systemBytes)
        => HsmsMessage.Control(HsmsMessage.AnySession, 0, 0, HsmsControlKind.DeselectRequest, systemBytes);

    /// <summary><c>Deselect.rsp</c>。</summary>
    internal static HsmsMessage DeselectResponse(uint systemBytes)
        => HsmsMessage.Control(HsmsMessage.AnySession, 0, 0, HsmsControlKind.DeselectResponse, systemBytes);

    /// <summary><c>Linktest.req</c>：链路测试请求。</summary>
    internal static HsmsMessage LinktestRequest(uint systemBytes)
        => HsmsMessage.Control(HsmsMessage.AnySession, 0, 0, HsmsControlKind.LinktestRequest, systemBytes);

    /// <summary><c>Linktest.rsp</c>：链路测试应答，System Bytes 与请求相同。</summary>
    internal static HsmsMessage LinktestResponse(uint systemBytes)
        => HsmsMessage.Control(HsmsMessage.AnySession, 0, 0, HsmsControlKind.LinktestResponse, systemBytes);

    /// <summary><c>Reject.req</c>：拒绝一条消息，头字节 2 是被拒消息的 SType 或 PType，头字节 3 是原因码。</summary>
    /// <param name="sessionId">会话号：回送被拒消息的会话号（数据消息是设备号）。</param>
    /// <param name="offendingType">被拒消息的 SType；<paramref name="reason"/> 为 <see cref="HsmsRejectReason.PTypeNotSupported"/> 时传它的 PType。</param>
    /// <param name="reason">原因码。</param>
    /// <param name="systemBytes">System Bytes，与被拒消息相同（拒绝的是我方发出的消息，带同样的号）。</param>
    internal static HsmsMessage Reject(ushort sessionId, byte offendingType, HsmsRejectReason reason, uint systemBytes)
        => HsmsMessage.Control(sessionId, offendingType, (byte)reason, HsmsControlKind.RejectRequest, systemBytes);

    /// <summary>
    /// 拒绝一条 PType 不是 0 的报文：<c>Reject.req</c> 原因 2，头字节 2 是它的 PType，会话号与 System Bytes 原样回送
    /// （SEMI E37、P51-D12）。头部不完整或 PType 就是 0 时返回 null。
    /// </summary>
    /// <param name="frame">读不出来的完整报文。</param>
    /// <returns>拒绝报文；不适用时为 null。</returns>
    internal static HsmsMessage? RejectUnsupportedPType(byte[] frame)
        => HsmsMessage.TryPeekUnsupportedPType(frame, out ushort sessionId, out byte pType, out uint systemBytes)
            ? Reject(sessionId, pType, HsmsRejectReason.PTypeNotSupported, systemBytes)
            : null;

    /// <summary><c>Separate.req</c>：会话分离，不等应答。</summary>
    internal static HsmsMessage SeparateRequest(uint systemBytes)
        => HsmsMessage.Control(HsmsMessage.AnySession, 0, 0, HsmsControlKind.SeparateRequest, systemBytes);
}