namespace Junevy.Communication.WebApi.Serialization;

/// <summary>
/// 业务包络配置的不可变快照（内部）：字段路径已拆成段，成功码放进集合。由 REST 的配置校验在校验通过后创建，构造后不再读取调用方的 <see cref="ResponseEnvelopeOptions"/>。
/// </summary>
internal sealed class EnvelopeSettings
{
    private static readonly string[] NoPath = Array.Empty<string>();

    /// <summary>复制已校验的配置；未启用时路径与成功码都为空，不做任何解析。</summary>
    /// <param name="options">已通过校验的包络配置。</param>
    public EnvelopeSettings(ResponseEnvelopeOptions options)
    {
        Enabled = options.Enabled;
        CodePath = Enabled ? Parse(options.CodePath) : NoPath;
        MessagePath = Enabled ? Parse(options.MessagePath) : NoPath;
        DataPath = Enabled ? Parse(options.DataPath) : NoPath;
        SuccessCodes = Enabled ? new HashSet<string>(options.SuccessCodes, StringComparer.Ordinal) : new HashSet<string>();
    }

    /// <summary>是否启用包络。</summary>
    public bool Enabled { get; }

    /// <summary>业务码的路径段。</summary>
    public IReadOnlyList<string> CodePath { get; }

    /// <summary>消息的路径段。</summary>
    public IReadOnlyList<string> MessagePath { get; }

    /// <summary>数据的路径段。</summary>
    public IReadOnlyList<string> DataPath { get; }

    /// <summary>表示成功的业务码（按 JSON 原文比较）。</summary>
    public IReadOnlyCollection<string> SuccessCodes { get; }

    /// <summary>
    /// 把点分路径拆成段。路径不能为空，也不能含空段（<c>a..b</c>、首尾的点）。路径规则只在这里定一次，配置校验与快照共用。
    /// </summary>
    /// <param name="path">点分路径，例如 <c>result.code</c>。</param>
    /// <param name="segments">拆出的路径段；路径非法时为空数组。</param>
    /// <returns>路径是否合法。</returns>
    public static bool TryParsePath(string? path, out string[] segments)
    {
        string[] parts = path == null ? NoPath : path.Split('.');
        bool valid = parts.Length > 0 && parts.All(part => !string.IsNullOrWhiteSpace(part));
        segments = valid ? parts : NoPath;
        return valid;
    }

    private static string[] Parse(string path)
    {
        TryParsePath(path, out string[] segments);
        return segments;
    }
}
