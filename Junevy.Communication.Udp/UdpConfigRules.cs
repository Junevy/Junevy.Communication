using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Udp;

/// <summary>
/// UDP 配置的通用校验规则（内部）：地址与主机名解析、组播判定、数值范围。由 <see cref="UdpChannelSettings"/> 使用（P25 Task 9）。
/// 消息文本与参数名与拆分前保持一致。
/// </summary>
internal static class UdpConfigRules
{
    // 主机名可以是 IP 字面量或语法合法的 DNS 名称；不在此解析 DNS（构造期间不做网络 I/O，解析在打开时进行）。
    public static bool IsValidHost(string host)
        => IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns;

    public static IPAddress[] ParseGroups(string[]? texts)
    {
        if (texts == null)
            return Array.Empty<IPAddress>();

        var groups = new IPAddress[texts.Length];
        for (int i = 0; i < texts.Length; i++)
        {
            IPAddress address = ParseAddress(texts[i], nameof(UdpChannelConfig.MulticastGroups));
            if (!IsMulticast(address))
                throw new ArgumentException($"MulticastGroups entry '{texts[i]}' is not a multicast address.");

            groups[i] = address;
        }

        return groups;
    }

    public static IPAddress ParseAddress(string? text, string property)
    {
        if (string.IsNullOrWhiteSpace(text) || !IPAddress.TryParse(text, out IPAddress? address))
            throw new ArgumentException($"{property} must be an IP address literal, not '{text}'.");

        return address!;
    }

    // IPv4 组播为 224.0.0.0/4，IPv6 组播为 ff00::/8。
    public static bool IsMulticast(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6Multicast;

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] >= 224 && bytes[0] <= 239;
    }

    public static void InRange(int value, int min, int max, string property)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(property, value, $"{property} must be within [{min}, {max}].");
    }

    public static void Positive(int value, string property)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(property, value, $"{property} must be positive.");
    }

    public static void NonNegative(int value, string property)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(property, value, $"{property} must not be negative.");
    }
}
