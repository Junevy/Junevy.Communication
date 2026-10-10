using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// Plc 测试共用的辅助方法：把测试设备接到脚本化通道上，并把记录的请求解码为（操作、编号、点数）。
/// </summary>
internal static class PlcTestHelpers
{
    /// <summary>创建一个连接状态的通道，每个请求由测试设备应答。</summary>
    /// <param name="device">测试设备。</param>
    /// <returns>脚本化通道。</returns>
    public static ScriptedClientChannel ChannelFor(FakeDevice device)
        => new ScriptedClientChannel { Responder = request => CommResult<byte[]>.Success(device.Respond(request)) };

    /// <summary>每个已发送请求的（操作、编号、点数），按发送顺序。</summary>
    /// <param name="channel">记录请求的通道。</param>
    /// <returns>请求的头部信息。</returns>
    public static (byte Op, int Number, int Count)[] Headers(ScriptedClientChannel channel)
        => channel.Requests.Select(request =>
        {
            var header = FakeFrame.ReadHeader(request);
            return (header.Op, header.Number, header.Count);
        }).ToArray();
}
