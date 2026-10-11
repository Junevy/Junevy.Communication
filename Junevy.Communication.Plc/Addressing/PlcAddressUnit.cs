namespace Junevy.Communication.Plc;

/// <summary>地址的单位：位软元件、字软元件还是字节软元件。</summary>
public enum PlcAddressUnit
{
    /// <summary>位软元件（例如 MELSEC 的 M、X、Y）：一个地址对应一个位。</summary>
    Bit,

    /// <summary>字软元件（例如 MELSEC 的 D、W）：一个地址对应一个 16 位字。</summary>
    Word,

    /// <summary>
    /// 字节软元件（例如 S7 的 MB、DBB）：一个地址对应一个字节。按字访问时，一个字等于从该地址起的连续 2 个字节；
    /// 带位号时位号为 0–7。
    /// </summary>
    Byte,
}
