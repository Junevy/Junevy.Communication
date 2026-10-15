using Opc.Ua;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// NodeId 文本 → SDK 的 <see cref="NodeId"/>（P62-D5）。
/// SDK 的 <c>NodeId.Parse</c> 不认识 <c>nsu=</c> 长形式，也不拒绝空串（空串会被解析成 <c>i=0</c>），
/// 因此这两点由本库自己处理。
/// </summary>
internal static class OpcUaSdkNodeId
{
    private const string NamespaceUriPrefix = "nsu=";

    /// <summary>
    /// 解析一段 NodeId 文本。
    /// </summary>
    /// <param name="text">NodeId 文本。</param>
    /// <param name="namespaceUris">会话的命名空间表；<c>nsu=</c> 形式需要它。</param>
    /// <param name="nodeId">解析出的 NodeId。</param>
    /// <param name="reason">失败原因，成功时为 null。</param>
    /// <returns>文本合法时为 true。</returns>
    public static bool TryParse(string? text, NamespaceTable? namespaceUris, out NodeId nodeId, out string? reason)
    {
        nodeId = NodeId.Null;

        if (string.IsNullOrWhiteSpace(text))
        {
            reason = "The node id is empty.";
            return false;
        }

        try
        {
            // 命名空间 URI 形式只能用长形式解析：NodeId.Parse 不认识 nsu=（计划第 19.2.13 节的 F4）。
            nodeId = text.StartsWith(NamespaceUriPrefix, StringComparison.Ordinal)
                ? ParseLongForm(text, namespaceUris)
                : NodeId.Parse(text);

            reason = null;
            return true;
        }
        catch (Exception exception) when (exception is ServiceResultException or ArgumentException)
        {
            reason = $"The node id '{text}' is not valid: {exception.Message}";
            return false;
        }
    }

    private static NodeId ParseLongForm(string text, NamespaceTable? namespaceUris)
        => namespaceUris is null
            ? throw new ServiceResultException(StatusCodes.BadInvalidArgument, "An nsu= node id needs a session namespace table.")
            : NodeId.ParseLongForm(text, namespaceUris);
}