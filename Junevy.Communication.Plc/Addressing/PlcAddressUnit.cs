namespace Junevy.Communication.Plc;

/// <summary>地址的单位：位软元件还是字软元件。</summary>
public enum PlcAddressUnit
{
    /// <summary>位软元件（例如 MELSEC 的 M、X、Y）：一个地址对应一个位。</summary>
    Bit,

    /// <summary>字软元件（例如 MELSEC 的 D、W）：一个地址对应一个 16 位字。</summary>
    Word,
}
