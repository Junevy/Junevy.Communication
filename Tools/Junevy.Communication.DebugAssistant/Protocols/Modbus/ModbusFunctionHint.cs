namespace Junevy.Communication.DebugAssistant.Protocols.Modbus;

/// <summary>
/// 功能码提示（计划第 7.2 节）：读取与写入使用的功能码，由存储区、数量与"单个写入功能码"选项决定。
/// 每个报文都有自己的上限（读寄存器 125、读位 2000、写寄存器 123、写线圈 1968）；单个写入只作用于单点的报文。
/// </summary>
public static class ModbusFunctionHint
{
    /// <summary>写入寄存器每个报文的上限（字）。</summary>
    public const int WriteRegistersPerPacket = 123;

    /// <summary>写入线圈每个报文的上限（点）。</summary>
    public const int WriteCoilsPerPacket = 1968;

    /// <summary>读取的功能码：C 为 01，DI 为 02，HR 为 03，IR 为 04；其他存储区没有读取功能码。</summary>
    public static byte? ReadFunction(string symbol) => symbol switch
    {
        "C" => 0x01,
        "DI" => 0x02,
        "HR" => 0x03,
        "IR" => 0x04,
        _ => null,
    };

    /// <summary>写入的报文：每个报文的数量与功能码。只读区域（DI、IR）没有写入报文。</summary>
    public static IReadOnlyList<(int Count, byte FunctionCode)> WritePackets(string symbol, int count, bool useSingleWriteFunctions)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "数量必须大于 0。");
        }

        bool coils = symbol == "C";
        if (!coils && symbol != "HR")
        {
            return [];
        }

        int limit = coils ? WriteCoilsPerPacket : WriteRegistersPerPacket;
        byte multiple = coils ? (byte)0x0F : (byte)0x10;
        byte single = coils ? (byte)0x05 : (byte)0x06;
        var packets = new List<(int Count, byte FunctionCode)>();
        int remaining = count;
        while (remaining > 0)
        {
            int size = Math.Min(remaining, limit);
            // 单个写入只用于单点的报文；库对每个报文独立判断。
            byte function = useSingleWriteFunctions && size == 1 ? single : multiple;
            packets.Add((size, function));
            remaining -= size;
        }

        return packets;
    }

    /// <summary>界面的提示文字。只读区域说明写入会被拒绝，且不发送报文。</summary>
    public static string Describe(string symbol, int count, bool useSingleWriteFunctions)
    {
        if (ReadFunction(symbol) is { } read)
        {
            string writeText = symbol is "DI" or "IR"
                ? "写入会被拒绝，不发送报文。"
                : string.Join("；", WritePackets(symbol, count, useSingleWriteFunctions)
                    .Select(packet => $"{packet.Count} 个用 {packet.FunctionCode:X2}"));
            return $"读取功能码 {read:X2}。写入：{writeText}";
        }

        return "未知的存储区。";
    }
}
