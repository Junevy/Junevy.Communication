namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// HSMS 控制消息的 SType（SEMI E37、P51-D6）。数据消息的 SType 是 0，因此 <see cref="None"/> 表示"不是控制消息"。
/// </summary>
internal enum HsmsControlKind
{
    /// <summary>数据消息（SECS-II），没有控制语义。</summary>
    None = 0,

    /// <summary><c>Select.req</c>：发起选择（HSMS-SS 里只有 Active 一方发起）。</summary>
    SelectRequest = 1,

    /// <summary><c>Select.rsp</c>：应答选择，头字节 3 是状态码（0 为成功，1 为已激活）。</summary>
    SelectResponse = 2,

    /// <summary><c>Deselect.req</c>：解除选择；HSMS-SS 不用它。</summary>
    DeselectRequest = 3,

    /// <summary><c>Deselect.rsp</c>：应答解除选择。</summary>
    DeselectResponse = 4,

    /// <summary><c>Linktest.req</c>：链路测试请求。</summary>
    LinktestRequest = 5,

    /// <summary><c>Linktest.rsp</c>：链路测试应答。</summary>
    LinktestResponse = 6,

    /// <summary><c>Reject.req</c>：拒绝，头字节 2 是被拒消息的 SType（原因 2 时是被拒消息的 PType），头字节 3 是原因码。</summary>
    RejectRequest = 7,

    /// <summary><c>Separate.req</c>：会话分离，不等应答。</summary>
    SeparateRequest = 9,
}

/// <summary>
/// <c>Reject.req</c> 的原因码（SEMI E37、P51-D11、D12）。本库只用到前四个。
/// </summary>
internal enum HsmsRejectReason : byte
{
    /// <summary>不理解：SType 未定义，或 HSMS-SS 不支持它（例如 <c>Deselect.req</c>）。</summary>
    NotUnderstood = 1,

    /// <summary>PType 不受支持；本库只接受 PType 0。</summary>
    PTypeNotSupported = 2,

    /// <summary>事务未打开：收到一个没有对应在途事务的 <c>*.rsp</c>。</summary>
    TransactionNotOpen = 3,

    /// <summary>未选中：还没 Select 就收到了数据消息。</summary>
    NotSelected = 4,
}