using System.Collections.Concurrent;
using Junevy.Communication.DebugAssistant.Tests.Support;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Channels;

/// <summary>
/// 定时循环发送（计划第 7.13 节、Task 5 第 2 条）：按次数结束、被停止、随断开结束后都不再发送。
/// 服务端使用定长 1 字节分帧，每个负载恰好是一帧，因此收到的帧数就是发送的次数。
/// </summary>
public sealed class ChannelPeriodicTests
{
    [Fact]
    public async Task PeriodicSend_StopsAfterTheCount_AndSendsNoMore()
    {
        var dispatcher = new ManualUiDispatcher();
        var frames = new ConcurrentQueue<byte[]>();
        await using var server = await ChannelTestKit.StartServerAsync(ChannelTestKit.FixedLength(1), (_, data) =>
        {
            frames.Enqueue(data);
            return Task.CompletedTask;
        });
        await using var page = ChannelTestKit.TcpPage(dispatcher, server.Config.Port);
        await page.ConnectCommand.ExecuteAsync(null);
        page.PayloadText = "hex:41";
        page.PeriodicIntervalMs = 10;
        page.PeriodicCount = 3;

        page.StartPeriodicCommand.Execute(null);
        await Waiting.UntilAsync(() => !page.IsPeriodicRunning, TimeSpan.FromSeconds(5), dispatcher.RunPending);
        await Waiting.UntilAsync(() => frames.Count == 3, TimeSpan.FromSeconds(5));
        await Task.Delay(150);

        Assert.Equal(3, frames.Count);
    }

    [Fact]
    public async Task PeriodicSend_StopCommand_StopsFurtherSends_AndRefreshesTheCommands()
    {
        var dispatcher = new ManualUiDispatcher();
        var frames = new ConcurrentQueue<byte[]>();
        await using var server = await ChannelTestKit.StartServerAsync(ChannelTestKit.FixedLength(1), (_, data) =>
        {
            frames.Enqueue(data);
            return Task.CompletedTask;
        });
        await using var page = ChannelTestKit.TcpPage(dispatcher, server.Config.Port);
        await page.ConnectCommand.ExecuteAsync(null);
        page.PayloadText = "hex:41";
        page.PeriodicIntervalMs = 10;
        page.PeriodicCount = 0;
        int refreshes = 0;
        page.StartPeriodicCommand.CanExecuteChanged += (_, _) => refreshes++;

        page.StartPeriodicCommand.Execute(null);
        Assert.True(page.IsPeriodicRunning);
        Assert.False(page.StartPeriodicCommand.CanExecute(null));
        Assert.True(refreshes > 0, "运行状态变化时，开始与停止命令要通知界面刷新。");
        await Waiting.UntilAsync(() => frames.Count >= 3, TimeSpan.FromSeconds(5));

        page.StopPeriodicCommand.Execute(null);

        Assert.False(page.IsPeriodicRunning);
        Assert.True(page.StartPeriodicCommand.CanExecute(null));
        await Task.Delay(100);
        int afterStop = frames.Count;
        await Task.Delay(200);
        Assert.Equal(afterStop, frames.Count);
    }

    [Fact]
    public async Task PeriodicSend_StopsWhenTheChannelIsDisconnected()
    {
        var dispatcher = new ManualUiDispatcher();
        var frames = new ConcurrentQueue<byte[]>();
        await using var server = await ChannelTestKit.StartServerAsync(ChannelTestKit.FixedLength(1), (_, data) =>
        {
            frames.Enqueue(data);
            return Task.CompletedTask;
        });
        await using var page = ChannelTestKit.TcpPage(dispatcher, server.Config.Port);
        await page.ConnectCommand.ExecuteAsync(null);
        page.PayloadText = "hex:41";
        page.PeriodicIntervalMs = 10;
        page.PeriodicCount = 0;
        page.StartPeriodicCommand.Execute(null);
        await Waiting.UntilAsync(() => frames.Count >= 2, TimeSpan.FromSeconds(5));

        await page.DisconnectCommand.ExecuteAsync(null);

        Assert.False(page.IsPeriodicRunning);
        await Task.Delay(100);
        int afterDisconnect = frames.Count;
        await Task.Delay(200);
        Assert.Equal(afterDisconnect, frames.Count);
    }
}
