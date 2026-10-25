using Junevy.Communication.Core.Results;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>
/// 假 MC 设备：按 3E 二进制帧应答批量读取（命令 0401）。应答头照抄请求的访问路径；数据为字，
/// 第 i 个字的值为 <c>0x1000 + 起始软元件号 + i</c>。请求的起始软元件号在第 15 至 17 字节，点数在第 19 至 20 字节。
/// </summary>
internal static class FakeMcDevice
{
    public static CommResult<byte[]> Respond(byte[] request)
    {
        int head = request[15] | (request[16] << 8) | (request[17] << 16);
        int points = request[19] | (request[20] << 8);
        int length = 2 + (points * 2);
        var frame = new List<byte>
        {
            0xD0, 0x00, request[2], request[3], request[4], request[5], request[6],
            (byte)length, (byte)(length >> 8), 0x00, 0x00,
        };
        for (int index = 0; index < points; index++)
        {
            ushort value = (ushort)(0x1000 + head + index);
            frame.Add((byte)value);
            frame.Add((byte)(value >> 8));
        }

        return CommResult<byte[]>.Success([.. frame]);
    }
}
