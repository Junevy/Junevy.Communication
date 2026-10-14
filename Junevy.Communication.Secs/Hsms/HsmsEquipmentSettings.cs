using Junevy.Communication.Secs.Protocol;

namespace Junevy.Communication.Secs.Hsms;

/// <summary>
/// 设备端的已校验运行参数（内部，P51-D5、D9）。<see cref="HsmsEquipment.Create"/> 校验之后取快照，
/// 之后不再读取调用方的选项对象。公开的选项类型仍然是 <see cref="HsmsEquipmentOptions"/>。
/// </summary>
internal sealed class HsmsEquipmentSettings
{
    /// <summary>设备号的上限（SEMI E37：会话号的最高位留给控制消息的 0xFFFF）。</summary>
    private const ushort MaximumDeviceId = 32767;

    /// <summary>监听地址缺省值：所有地址。</summary>
    private const string AnyAddress = "0.0.0.0";

    /// <summary>消息长度的下限：再小就容不下一条 SECS-II 数据项加它的头部。</summary>
    private const int MinimumMessageLength = 1024;

    private const int MinimumPort = 1;
    private const int MaximumPort = 65535;

    private HsmsEquipmentSettings(HsmsConnectionMode mode, string? address, int port, ushort deviceId, HsmsEquipmentTimers timers)
    {
        Mode = mode;
        Address = address;
        Port = port;
        DeviceId = deviceId;
        Timers = timers;
    }

    /// <summary>设备端在 HSMS 层的角色。</summary>
    internal HsmsConnectionMode Mode { get; }

    /// <summary>地址：被动端是监听地址，主动端是对端地址（已校验非空）。</summary>
    internal string? Address { get; }

    /// <summary>端口。</summary>
    internal int Port { get; }

    /// <summary>设备号（HSMS 的会话号）。</summary>
    internal ushort DeviceId { get; }

    /// <summary>计时器与上限的快照。</summary>
    internal HsmsEquipmentTimers Timers { get; }

    /// <summary>校验并取快照。</summary>
    /// <param name="options">调用方的选项。</param>
    /// <returns>快照。</returns>
    /// <exception cref="ArgumentException">任一属性越界、角色未知或地址非法。</exception>
    internal static HsmsEquipmentSettings Validate(HsmsEquipmentOptions options)
    {
        if (!Enum.IsDefined(typeof(HsmsConnectionMode), options.Mode))
        {
            throw new ArgumentException(
                $"Mode must be Passive or Active, but it is {options.Mode}.",
                nameof(HsmsEquipmentOptions.Mode));
        }

        string? address = ValidateAddress(options);
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

        return new HsmsEquipmentSettings(options.Mode, address, options.Port, options.DeviceId, timers);
    }

    // 被动端：本机的监听地址，只能是 IP，null 表示所有地址。
    // 主动端：对端地址，必填；与其他 TCP 客户端一样可以是主机名，由通道解析。
    private static string? ValidateAddress(HsmsEquipmentOptions options)
    {
        if (options.Mode == HsmsConnectionMode.Active)
        {
            if (string.IsNullOrWhiteSpace(options.Host))
            {
                throw new ArgumentException(
                    "Host is required in the active mode: it is the address of the host to connect to.",
                    nameof(HsmsEquipmentOptions.Host));
            }

            return options.Host;
        }

        if (options.Host is null)
            return AnyAddress;

        if (!System.Net.IPAddress.TryParse(options.Host, out _))
        {
            throw new ArgumentException(
                "In the passive mode Host is the local address to listen on: an IP address, or null for every address.",
                nameof(HsmsEquipmentOptions.Host));
        }

        return options.Host;
    }

    private static void Require(int value, int min, int max, string name)
    {
        if (value < min || value > max)
            throw new ArgumentException($"{name} must be within [{min}, {max}], but it is {value}.", name);
    }
}
