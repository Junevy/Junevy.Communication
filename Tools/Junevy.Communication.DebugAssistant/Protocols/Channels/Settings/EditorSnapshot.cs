using System.Globalization;
using System.Reflection;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Settings;

/// <summary>
/// 编辑器的参数快照：把编辑器上每个可读写的属性与文本互相转换（工作区与导出使用，计划 DA-D11）。
/// 按反射遍历编辑器自己声明的属性，因此新增的字段自动被保存；测试锁定每个字段都能往返。
/// </summary>
internal static class EditorSnapshot
{
    private const BindingFlags DeclaredPublicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    public static Dictionary<string, string> Capture(object editor)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (PropertyInfo property in Properties(editor))
        {
            values[property.Name] = Format(property.GetValue(editor));
        }

        return values;
    }

    public static void Apply(object editor, IReadOnlyDictionary<string, string> values)
    {
        foreach (PropertyInfo property in Properties(editor))
        {
            if (values.TryGetValue(property.Name, out string? text))
            {
                property.SetValue(editor, Parse(property.PropertyType, text));
            }
        }
    }

    private static IEnumerable<PropertyInfo> Properties(object editor) => editor.GetType()
        .GetProperties(DeclaredPublicInstance)
        .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0);

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        Enum enumValue => enumValue.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static object? Parse(Type type, string text)
    {
        if (type == typeof(string))
        {
            return text;
        }

        if (type == typeof(bool))
        {
            return bool.Parse(text);
        }

        if (type == typeof(int))
        {
            return int.Parse(text, CultureInfo.InvariantCulture);
        }

        if (type.IsEnum)
        {
            return Enum.Parse(type, text, ignoreCase: true);
        }

        throw new NotSupportedException($"A setting of type {type.Name} cannot be restored.");
    }
}
