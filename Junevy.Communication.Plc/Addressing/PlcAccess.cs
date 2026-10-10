namespace Junevy.Communication.Plc;

/// <summary>一次请求的访问粒度：以字为单位，还是以位（点）为单位。</summary>
public enum PlcAccess
{
    /// <summary>以字为单位访问；位软元件按 16 点为一字。</summary>
    Words,

    /// <summary>以位（点）为单位访问。</summary>
    Bits,
}
