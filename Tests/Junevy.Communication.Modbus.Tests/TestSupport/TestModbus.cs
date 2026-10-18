using System.Buffers;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Modbus.Framing;
using Junevy.Communication.Modbus.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests;

/// <summary>
/// Modbus 客户端测试的辅助：构造客户端、从线上的请求取出 PDU 与站号、按请求的帧格式生成应答。
/// </summary>
internal static class TestModbus
{
    /// <summary>用脚本通道构造客户端（不经过 Create，因此不需要真实的传输）。</summary>
    /// <param name="channel">测试通道。</param>
    /// <param name="framing">帧格式。</param>
    /// <param name="options">选项；为 null 时使用默认选项。</param>
    /// <returns>客户端。</returns>
    public static ModbusClient Client(ScriptedModbusChannel channel, ModbusFraming framing = ModbusFraming.Mbap, ModbusClientOptions? options = null)
    {
        ModbusClientOptions effective = options ?? new ModbusClientOptions();
        effective.Framing ??= framing;
        return new ModbusClient(channel, ModbusProtocolSettings.From(effective), framing, effective, null);
    }

    /// <summary>从线上的请求取出 PDU（不含站号、MBAP 头与 CRC）。</summary>
    public static byte[] Pdu(byte[] request, ModbusFraming framing)
        => framing == ModbusFraming.Mbap
            ? request.Skip(7).ToArray()
            : request.Skip(1).Take(request.Length - 3).ToArray();

    /// <summary>从线上的请求取出站号。</summary>
    public static byte Unit(byte[] request, ModbusFraming framing) => framing == ModbusFraming.Mbap ? request[6] : request[0];

    /// <summary>把 PDU 封装成与请求同一帧格式的应答。MBAP 的事务号取自请求。</summary>
    public static byte[] Frame(byte[] request, ModbusFraming framing, byte unit, byte[] pdu)
        => Frame((ushort)((request[0] << 8) | request[1]), framing, unit, pdu);

    /// <summary>按给定的事务号封装 PDU（RTU 没有事务号，忽略它）。</summary>
    public static byte[] Frame(ushort transactionId, ModbusFraming framing, byte unit, byte[] pdu)
    {
        using var buffer = new ByteBufferWriter();
        if (framing == ModbusFraming.Mbap)
        {
            MbapHeader.Write(buffer, transactionId, unit, pdu.Length);
            ModbusBuffer.Write(buffer, pdu);
        }
        else
        {
            RtuAdu.Write(buffer, unit, pdu);
        }

        return buffer.ToArray();
    }

    /// <summary>总是返回同一个 PDU 的应答。</summary>
    public static Func<byte[], CommResult<byte[]>?> Responds(ModbusFraming framing, string pduHex, byte unit = 1)
        => request => CommResult<byte[]>.Success(Frame(request, framing, unit, HexVector.ToBytes(pduHex)));

    /// <summary>写入的应答：回显请求的数据（05、06、16 回显全部，0F、10 只回显前 4 字节）。</summary>
    public static Func<byte[], CommResult<byte[]>?> Echoes(ModbusFraming framing, byte unit = 1)
        => request =>
        {
            byte[] pdu = Pdu(request, framing);
            int length = pdu[0] is 0x0F or 0x10 ? 5 : pdu.Length;
            return CommResult<byte[]>.Success(Frame(request, framing, unit, pdu.Take(length).ToArray()));
        };

    /// <summary>读字的应答：数据长度由请求的字数决定，内容为 0。</summary>
    public static Func<byte[], CommResult<byte[]>?> ReadsWords(ModbusFraming framing, byte unit = 1)
        => request =>
        {
            byte[] pdu = Pdu(request, framing);
            int quantity = (pdu[3] << 8) | pdu[4];
            var reply = new byte[2 + quantity * 2];
            reply[0] = pdu[0];
            reply[1] = (byte)(quantity * 2);
            return CommResult<byte[]>.Success(Frame(request, framing, unit, reply));
        };

    /// <summary>读位的应答：数据长度由请求的点数决定，内容为 0。</summary>
    public static Func<byte[], CommResult<byte[]>?> ReadsBits(ModbusFraming framing, byte unit = 1)
        => request =>
        {
            byte[] pdu = Pdu(request, framing);
            int quantity = (pdu[3] << 8) | pdu[4];
            int bytes = (quantity + 7) / 8;
            var reply = new byte[2 + bytes];
            reply[0] = pdu[0];
            reply[1] = (byte)bytes;
            return CommResult<byte[]>.Success(Frame(request, framing, unit, reply));
        };
}
