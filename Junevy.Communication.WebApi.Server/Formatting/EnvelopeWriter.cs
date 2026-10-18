using System.Buffers;
using System.Text.Json;
using Junevy.Communication.WebApi.Server.Config;

namespace Junevy.Communication.WebApi.Server.Formatting;

/// <summary>
/// 业务包络的写出（内部，P32-D6）：字段按配置的点分路径放进嵌套对象，例如 <c>result.code</c> 写成 <c>{"result":{"code":...}}</c>。
/// 数据是序列化器输出的 JSON，原样嵌入，不再解析；成功写 <c>{ code, msg, data }</c>，失败写 <c>{ code, msg }</c>。
/// </summary>
internal static class EnvelopeWriter
{
    /// <summary>写出成功的包络；<paramref name="dataJson"/> 为 null 时 <c>data</c> 写 JSON null。</summary>
    /// <param name="envelope">包络的字段路径与成功码。</param>
    /// <param name="dataJson">数据的 JSON 字节；可为 null。</param>
    /// <returns>UTF-8 JSON 字节。</returns>
    public static byte[] WriteSuccess(ServerEnvelopeSettings envelope, byte[]? dataJson)
        => Write(
        [
            new Field(envelope.CodePath, envelope.SuccessCode, null),
            new Field(envelope.MessagePath, string.Empty, null),
            new Field(envelope.DataPath, null, dataJson),
        ]);

    /// <summary>写出失败的包络（不含 data）。</summary>
    /// <param name="envelope">包络的字段路径。</param>
    /// <param name="code">业务码。</param>
    /// <param name="message">消息。</param>
    /// <returns>UTF-8 JSON 字节。</returns>
    public static byte[] WriteFailure(ServerEnvelopeSettings envelope, string code, string message)
        => Write(
        [
            new Field(envelope.CodePath, code, null),
            new Field(envelope.MessagePath, message, null),
        ]);

    private static byte[] Write(IReadOnlyList<Field> fields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteLevel(writer, fields, 0);
        }

        return buffer.WrittenSpan.ToArray();
    }

    // 同一层的字段按路径段分组：路径相同的段只写一次属性名，共享前缀的字段进入同一个嵌套对象。
    private static void WriteLevel(Utf8JsonWriter writer, IReadOnlyList<Field> fields, int depth)
    {
        writer.WriteStartObject();
        foreach (IGrouping<string, Field> group in fields.GroupBy(field => field.Path[depth]))
        {
            writer.WritePropertyName(group.Key);
            Field[] members = group.ToArray();
            if (members.Length == 1 && members[0].Path.Count == depth + 1)
                WriteValue(writer, members[0]);
            else
                WriteLevel(writer, members, depth + 1);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, Field field)
    {
        if (field.Text != null)
            writer.WriteStringValue(field.Text);
        else if (field.Raw != null)
            writer.WriteRawValue(field.Raw, skipInputValidation: false);
        else
            writer.WriteNullValue();
    }

    // 一个字段：文本（写成 JSON 字符串）或原始 JSON（为 null 时写 JSON null）。
    private readonly record struct Field(IReadOnlyList<string> Path, string? Text, byte[]? Raw);
}
