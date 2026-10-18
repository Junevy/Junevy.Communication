namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// 重复下发去重的配置（P32-D11）。默认关闭；开启后按幂等键、方法与路径识别同一条命令。
/// </summary>
public sealed class WebApiServerDeduplicationOptions
{
    /// <summary>是否开启去重，默认 false。</summary>
    public bool Enabled { get; set; }

    /// <summary>携带幂等键的请求头名称，默认 <c>Idempotency-Key</c>。</summary>
    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>去重窗口（毫秒）：窗口内同键已完成的请求直接重放第一次的响应。默认 600000（10 分钟）。</summary>
    public int Window { get; set; } = 600000;

    /// <summary>去重条目的上限，满了先淘汰最旧的。默认 1000。</summary>
    public int MaxEntries { get; set; } = 1000;
}
