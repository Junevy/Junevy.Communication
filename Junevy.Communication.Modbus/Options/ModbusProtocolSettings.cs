using System.Text;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Plc;

namespace Junevy.Communication.Modbus;

/// <summary>
/// Modbus 客户端选项的快照：构造时校验并复制为不可变的值，之后修改选项对象不影响协议行为（与 MELSEC 的做法一致）。
/// </summary>
internal sealed class ModbusProtocolSettings
{
    private ModbusProtocolSettings(ModbusClientOptions options, PlcDataFormat dataFormat)
    {
        Framing = options.Framing;
        UnitId = options.UnitId;
        UseSingleWriteFunctions = options.UseSingleWriteFunctions;
        UseMaskWriteForBits = options.UseMaskWriteForBits;
        DataFormat = dataFormat;
    }

    /// <summary>帧格式；为 null 时由介质决定（见 <see cref="ModbusProtocolProfile"/>）。</summary>
    public ModbusFraming? Framing { get; }

    /// <summary>默认站号。</summary>
    public byte UnitId { get; }

    /// <summary>一个点或一个字的写入是否使用 05、06。</summary>
    public bool UseSingleWriteFunctions { get; }

    /// <summary>寄存器中的位写入是否使用掩码写。</summary>
    public bool UseMaskWriteForBits { get; }

    /// <summary>字序与字符串编码。</summary>
    public PlcDataFormat DataFormat { get; }

    /// <summary>校验选项并取快照。</summary>
    /// <param name="options">客户端选项。</param>
    /// <returns>快照。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">帧格式未定义。</exception>
    public static ModbusProtocolSettings From(ModbusClientOptions options)
    {
        if (options is null)
            throw new ArgumentNullException(nameof(options));
        if (options.Framing is { } framing && !Enum.IsDefined(typeof(ModbusFraming), framing))
            throw new ArgumentOutOfRangeException(nameof(options), framing, "The Modbus framing is not defined.");

        return new ModbusProtocolSettings(options, options.DataFormat ?? new PlcDataFormat(WordOrder.ABCD, Encoding.ASCII));
    }
}
