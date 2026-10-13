using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 被测的设备端：真实的 <see cref="HsmsEquipment"/>（监听）+ 测试侧的最小主机（连接）。
/// 计时器按计划第 1 节第 7 条调到几百毫秒。
/// </summary>
internal sealed class HsmsEquipmentRig : IAsyncDisposable
{
    private readonly HsmsEquipment equipment;

    private HsmsEquipmentRig(HsmsEquipment equipment, int port, TestLogger logger, HsmsStateLog states, CommResult start)
    {
        this.equipment = equipment;
        Port = port;
        Logger = logger;
        States = states;
        StartResult = start;
    }

    /// <summary>设备端监听的端口（主动端时是对端端口）。</summary>
    internal int Port { get; }

    /// <summary>设备端。</summary>
    internal HsmsEquipment Equipment => equipment;

    /// <summary><see cref="HsmsEquipment.StartAsync"/> 的结果（失败时不抛异常，用例据此断言）。</summary>
    internal CommResult StartResult { get; }

    /// <summary>状态变化记录（已订阅 <see cref="HsmsEquipment.StateChanged"/>）。</summary>
    internal HsmsStateLog States { get; }

    /// <summary>日志（设备端与通道共用一个收集器）。</summary>
    internal TestLogger Logger { get; }

    /// <summary>设备端的当前状态。</summary>
    internal HsmsState State => equipment.State;

    /// <summary>建设备端并开始监听（被动端）。</summary>
    /// <param name="configure">改选项（例如设备号或 T3）。</param>
    /// <returns>已监听的设备端；调用方负责释放。</returns>
    internal static Task<HsmsEquipmentRig> StartAsync(Action<HsmsEquipmentOptions>? configure = null)
    {
        HsmsEquipmentOptions options = FastOptions();
        configure?.Invoke(options);
        return StartWithAsync(options);
    }

    /// <summary>建设备端并去连主机（主动端）。</summary>
    /// <param name="port">主机的监听端口。</param>
    /// <param name="configure">改选项（例如 T5 或 T6）。</param>
    /// <returns>已连接的设备端；调用方负责释放。启动失败不在这里抛异常，结果见 <see cref="StartResult"/>。</returns>
    internal static Task<HsmsEquipmentRig> StartActiveAsync(int port, Action<HsmsEquipmentOptions>? configure = null)
    {
        HsmsEquipmentOptions options = FastOptions();
        options.Mode = HsmsConnectionMode.Active;
        options.Port = port;
        configure?.Invoke(options);
        return StartWithAsync(options);
    }

    /// <summary>主机连上设备端并完成 Select，返回主机一侧。</summary>
    /// <param name="rig">设备端。</param>
    /// <param name="systemBytes">Select.req 用的 System Bytes。</param>
    /// <returns>已选中的主机一侧；调用方负责释放。</returns>
    internal static async Task<HsmsHostConnector> SelectAsync(HsmsEquipmentRig rig, uint systemBytes = 1)
    {
        HsmsHostConnector host = await HsmsHostConnector.ConnectAsync(rig.Port).ConfigureAwait(false);
        await host.SendAsync(HsmsControl.SelectRequest(systemBytes).ToArray()).ConfigureAwait(false);
        HsmsHostFrame answer = await host.NextFrameAsync(5000).ConfigureAwait(false);
        if (answer.Message.Kind != HsmsControlKind.SelectResponse || answer.Message.Status != HsmsControl.SelectOk)
            throw new InvalidOperationException($"The equipment did not select the session: {answer.Text}");

        await Polling.WaitUntilAsync(() => rig.State == HsmsState.Selected, 5000).ConfigureAwait(false);
        return host;
    }

    /// <summary>主机连上设备端（不 Select）。</summary>
    /// <param name="rig">设备端。</param>
    /// <returns>主机一侧；调用方负责释放。</returns>
    internal static Task<HsmsHostConnector> ConnectAsync(HsmsEquipmentRig rig)
        => HsmsHostConnector.ConnectAsync(rig.Port);

    /// <summary>停止并释放设备端。可重复调用。</summary>
    public async ValueTask DisposeAsync() => await equipment.DisposeAsync().ConfigureAwait(false);

    // 两种模式共用这一段：建设备、订阅状态、启动；启动失败不抛，结果留给用例断言。
    private static async Task<HsmsEquipmentRig> StartWithAsync(HsmsEquipmentOptions options)
    {
        var logger = new TestLogger();
        HsmsEquipment equipment = HsmsEquipment.Create(options, logger);
        var states = new HsmsStateLog();
        equipment.StateChanged += states.Record;

        CommResult started = await Arrival.WithinAsync(equipment.StartAsync(), 10000).ConfigureAwait(false);
        if (!started.IsSuccess && options.Mode == HsmsConnectionMode.Passive)
            throw new InvalidOperationException($"The equipment did not start: {started.ErrorMessage}");

        return new HsmsEquipmentRig(equipment, options.Port, logger, states, started);
    }

    /// <summary>缩短的选项：计时器几百毫秒，端口用借来的空闲端口，设备号 1。</summary>
    /// <returns>选项。</returns>
    internal static HsmsEquipmentOptions FastOptions() => new HsmsEquipmentOptions
    {
        Host = LoopbackTcp.Host,
        Port = HsmsPorts.Free(),
        DeviceId = DefaultDeviceId,
        T3 = 500,
        T5 = 200,
        T6 = 400,
        T7 = 3000,
        T8 = 500,
        MaxMessageLength = HsmsTimers.MaxMessageLength,
    };

    /// <summary>测试里的设备号。</summary>
    internal const ushort DefaultDeviceId = 1;
}
