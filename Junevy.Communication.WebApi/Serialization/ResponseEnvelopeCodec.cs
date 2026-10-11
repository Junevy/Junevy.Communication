using System.Text.Json;

namespace Junevy.Communication.WebApi.Serialization;

/// <summary>
/// 业务包络的读写器（内部，P3-D15）。字段定位始终用 <see cref="JsonDocument"/>，与可替换的序列化器无关，业务码按 JSON 原文比较。
/// 本期只实现"读"（客户端解析 MES 的响应）；3.2 的服务端需要"写"时，在这个类里加写入方法即可，读取部分不用改。
/// </summary>
internal static class ResponseEnvelopeCodec
{
    /// <summary>
    /// 读出响应正文里的业务包络。正文不是 JSON、根不是对象、找不到可用的业务码都不抛异常，而是返回无效结果。
    /// 路径段按名称精确匹配（区分大小写）。
    /// </summary>
    /// <param name="body">响应正文（UTF-8 JSON）。</param>
    /// <param name="settings">包络配置快照。</param>
    /// <returns>读出的包络，或无法读出的原因。</returns>
    public static EnvelopeReadResult Read(byte[] body, EnvelopeSettings settings)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return ReadRoot(document.RootElement, settings);
        }
        catch (JsonException ex)
        {
            return EnvelopeReadResult.Invalid($"The response body is not valid JSON: {ex.Message}");
        }
    }

    private static EnvelopeReadResult ReadRoot(JsonElement root, EnvelopeSettings settings)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return EnvelopeReadResult.Invalid("The response body is not a JSON object.");

        if (!TryFind(root, settings.CodePath, out JsonElement code) || !IsScalar(code))
            return EnvelopeReadResult.Invalid($"The response envelope has no usable business code at '{string.Join(".", settings.CodePath)}'.");

        string? message = TryFind(root, settings.MessagePath, out JsonElement messageElement) ? Text(messageElement) : null;
        string? dataJson = TryFind(root, settings.DataPath, out JsonElement data) && data.ValueKind != JsonValueKind.Null ? data.GetRawText() : null;
        return EnvelopeReadResult.Valid(Text(code)!, message, dataJson);
    }

    private static bool TryFind(JsonElement root, IReadOnlyList<string> path, out JsonElement found)
    {
        found = root;
        foreach (string segment in path)
        {
            if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty(segment, out JsonElement next))
                return false;

            found = next;
        }

        return true;
    }

    // 业务码可以是字符串、数字或布尔；null、对象、数组都不能当业务码。
    private static bool IsScalar(JsonElement element)
        => element.ValueKind == JsonValueKind.String || element.ValueKind == JsonValueKind.Number
           || element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False;

    private static string? Text(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;

        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
    }
}
