using Junevy.Communication.WebApi;

namespace Junevy.Communication.WebApi.Server.Config;

/// <summary>
/// 业务包络配置的校验（内部）。未启用时不检查其余字段（与认证方式为 None 时一致），返回 <see cref="ServerEnvelopeSettings.Disabled"/>。
/// </summary>
internal static class ServerEnvelopeRules
{
    private const string Section = "config.Envelope";

    /// <summary>校验包络配置并生成快照。</summary>
    /// <param name="envelope">包络配置。</param>
    /// <returns>快照。</returns>
    /// <exception cref="ArgumentException">配置为 null，或启用后某个字段非法。</exception>
    public static ServerEnvelopeSettings Create(ResponseEnvelopeOptions? envelope)
    {
        if (envelope == null)
            throw new ArgumentException("config.Envelope must not be null.", Section);
        if (!envelope.Enabled)
            return ServerEnvelopeSettings.Disabled;

        string[] codePath = RequirePath(envelope.CodePath, nameof(ResponseEnvelopeOptions.CodePath));
        string[] messagePath = RequirePath(envelope.MessagePath, nameof(ResponseEnvelopeOptions.MessagePath));
        string[] dataPath = RequirePath(envelope.DataPath, nameof(ResponseEnvelopeOptions.DataPath));
        RequireDisjoint(nameof(ResponseEnvelopeOptions.CodePath), codePath, nameof(ResponseEnvelopeOptions.MessagePath), messagePath);
        RequireDisjoint(nameof(ResponseEnvelopeOptions.CodePath), codePath, nameof(ResponseEnvelopeOptions.DataPath), dataPath);
        RequireDisjoint(nameof(ResponseEnvelopeOptions.MessagePath), messagePath, nameof(ResponseEnvelopeOptions.DataPath), dataPath);
        string successCode = RequireSuccessCode(envelope.SuccessCodes);
        return new ServerEnvelopeSettings(true, codePath, messagePath, dataPath, successCode);
    }

    // 写出时同一层的字段按路径段分组：一条路径不能是另一条的前缀（包括相等），否则两个字段争用同一个属性。
    private static void RequireDisjoint(string firstProperty, string[] first, string secondProperty, string[] second)
    {
        if (IsPrefix(first, second) || IsPrefix(second, first))
        {
            throw new ArgumentException(
                $"{Section}.{firstProperty} and {Section}.{secondProperty} must not overlap, not '{string.Join(".", first)}' and '{string.Join(".", second)}'.",
                Section + "." + secondProperty);
        }
    }

    private static bool IsPrefix(string[] prefix, string[] path)
        => prefix.Length <= path.Length && prefix.SequenceEqual(path.Take(prefix.Length));

    private static string[] RequirePath(string? path, string property)
    {
        string[] parts = path == null ? Array.Empty<string>() : path.Split('.');
        if (parts.Length == 0 || parts.Any(part => string.IsNullOrWhiteSpace(part)))
        {
            string field = Section + "." + property;
            throw new ArgumentException($"{field} must be a dotted path without empty segments, not '{path}'.", field);
        }

        return parts;
    }

    private static string RequireSuccessCode(string[]? successCodes)
    {
        const string field = Section + "." + nameof(ResponseEnvelopeOptions.SuccessCodes);
        if (successCodes == null || successCodes.Length == 0)
            throw new ArgumentException($"{field} must contain at least one code.", field);
        if (successCodes.Any(string.IsNullOrEmpty))
            throw new ArgumentException($"{field} must not contain an empty code.", field);

        return successCodes[0];
    }
}
