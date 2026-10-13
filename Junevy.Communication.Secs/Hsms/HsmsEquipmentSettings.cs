using Junevy.Communication.Secs.Protocol;

namespace Junevy.Communication.Secs.Hsms;

/// <summary>
/// 设备端的已校验运行参数（内部，P51-D5、D9）。<see cref="HsmsEquipment.Create"/> 校验之后取快照，
/// 之后不再读取调用方的选项对象。公开的选项类型仍然是 <see cref="HsmsEquipmentOptions"/>。
/// </summary>
internal sealed class HsmsEquipmentSettings
{
    /// <summary>SEMI E37 的默认端口。</summary>
    private const ushort MaximumDeviceId = 32767;

    /// <summary>监听地址缺省值：所有地址。</summary>
    private const string AnyAddress = "0.0.0.0";

    /// <summary>消息长度的下限：再小就容不下一条 SECS-II 数据项加它的头部。</summary>
    private const int MinimumMessageLength = 1024;

    private const int MinimumPort = 1;
    private const int MaximumPort = 65535;

    private HsmsEquipmentSettings(string listenAddress, int port, ushort deviceId, HsmsEquipmentTimers timers)
    {
        ListenAddress = listenAddress;
        Port = port;
        DeviceId = deviceId;
        Timers = timers;
    }

    /// <summary>监听地址（被动端；Task 7 的主动端用它作为对端地址）。</summary>
    internal string ListenAddress { get; }

    /// <summary>端口。</summary>
    internal int Port { get; }

    /// <summary>设备号（HSMS 的会话号）。</summary>
    internal ushort DeviceId { get; }

    /// <summary>计时器与上限的快照。</summary>
    internal HsmsEquipmentTimers Timers { get; }

    /// <summary>校验并取快照。</summary>
    /// <param name="options">调用方的选项。</param>
    /// <returns>快照。</returns>
    /// <exception cref="ArgumentException">任一属性越界或地址非法。</exception>
    internal static HsmsEquipmentSettings Validate(HsmsEquipmentOptions options)
    {
        string listenAddress = ValidateAddress(options.Host);
        Require(options.Port, MinimumPort, MaximumPort, nameof(HsmsEquipmentOptions.Port));
        Require(options.DeviceId, 0, MaximumDeviceId, nameof(HsmsEquipmentOptions.DeviceId));
        Require(options.T3, 1, int.MaxValue, nameof(HsmsEquipmentOptions.T3));
        Require(options.T5, 1, int.MaxValue, nameof(HsmsEquipmentOptions.T5));
        Require(options.T6, 1, int.MaxValue, nameof(HsmsEquipmentOptions.T6));
        Require(options.T7, 1, int.MaxValue, nameof(HsmsEquipmentOptions.T7));
        Require(options.T8, 1, int.MaxValue, nameof(HsmsEquipmentOptions.T8));
        Require(options.LinktestInterval, 0, int.MaxValue, nameof(HsmsEquipmentOptions.LinktestInterval));
        Require(options.MaxMessageLength, MinimumMessageLength, int.MaxValue, nameof(HsmsEquipmentOptions.MaxMessageLength));

        var timers = new HsmsEquipmentTimers(
            options.T3,
            options.T5,
            options.T6,
            options.T7,
            options.T8,
            options.LinktestInterval,
            options.MaxMessageLength);

        return new HsmsEquipmentSettings(listenAddress, options.Port, options.DeviceId, timers);
    }

    // null 表示所有地址（只有 IP，不接受主机名：设备端的监听地址来自配置，不该依赖 DNS）。
    private static string ValidateAddress(string? host)
    {
        if (host is null)
            return AnyAddress;

        if (string.IsNullOrWhiteSpace(host) || !System.Net.IPAddress.TryParse(host, out _))
            throw new ArgumentException("Host must be an IP address, or null to listen on every address.", nameof(HsmsEquipmentOptions.Host));

        return host;
    }

    private static void Require(int value, int min, int max, string name)
    {
        if (value < min || value > max)
            throw new ArgumentException($"{name} must be within [{min}, {max}], but it is {value}.", name);
    }
}
