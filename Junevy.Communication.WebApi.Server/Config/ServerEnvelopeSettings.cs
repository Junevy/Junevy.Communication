namespace Junevy.Communication.WebApi.Server.Config;

/// <summary>
/// 业务包络的不可变快照（内部）：字段路径已拆成段，写出成功时使用的成功码取第一项。未启用时各项为空。
/// </summary>
internal sealed class ServerEnvelopeSettings
{
    /// <summary>未启用包络的快照。</summary>
    public static readonly ServerEnvelopeSettings Disabled = new ServerEnvelopeSettings(false, [], [], [], string.Empty);

    /// <summary>创建已经校验过的快照。</summary>
    /// <param name="enabled">是否启用。</param>
    /// <param name="codePath">业务码的路径段。</param>
    /// <param name="messagePath">消息的路径段。</param>
    /// <param name="dataPath">数据的路径段。</param>
    /// <param name="successCode">成功时写出的业务码。</param>
    internal ServerEnvelopeSettings(bool enabled, string[] codePath, string[] messagePath, string[] dataPath, string successCode)
    {
        Enabled = enabled;
        CodePath = codePath;
        MessagePath = messagePath;
        DataPath = dataPath;
        SuccessCode = successCode;
    }

    /// <summary>是否启用包络。</summary>
    public bool Enabled { get; }

    /// <summary>业务码的路径段，例如 <c>result.code</c> 为 <c>["result", "code"]</c>。</summary>
    public IReadOnlyList<string> CodePath { get; }

    /// <summary>消息的路径段。</summary>
    public IReadOnlyList<string> MessagePath { get; }

    /// <summary>数据的路径段。</summary>
    public IReadOnlyList<string> DataPath { get; }

    /// <summary>成功时写出的业务码（配置中 <c>SuccessCodes</c> 的第一项）。</summary>
    public string SuccessCode { get; }
}
