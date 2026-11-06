using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TCast.Infrastructure.Discovery;

/// <summary>
/// 可选局域网接口的一对 IPv4 地址与掩码。
/// </summary>
/// <param name="InterfaceId">接口稳定标识。</param>
/// <param name="DisplayName">展示名称。</param>
/// <param name="Address">本机 IPv4。</param>
/// <param name="Mask">子网掩码。</param>
public sealed record NetworkAdapter(string InterfaceId, string DisplayName, IPAddress Address, IPAddress Mask)
{
    /// <summary>定向广播地址，例如 192.168.4.10 / 255.255.255.0 → 192.168.4.255。</summary>
    public IPAddress Broadcast => ComputeBroadcast(Address, Mask);

    /// <summary>展示用文本。</summary>
    public string Display => $"{DisplayName} · {Address}";

    /// <summary>计算定向广播地址。</summary>
    /// <exception cref="ArgumentException">非 IPv4 地址或掩码。</exception>
    public static IPAddress ComputeBroadcast(IPAddress address, IPAddress mask)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(mask);
        if (address.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("需要 IPv4 地址与掩码");
        }

        byte[] ip = address.GetAddressBytes();
        byte[] netmask = mask.GetAddressBytes();
        for (int i = 0; i < 4; i++)
        {
            ip[i] = (byte)(ip[i] | ~netmask[i]);
        }

        return new IPAddress(ip);
    }

    /// <summary>枚举本机可用 IPv4 单播地址（排除回环与未启用接口）。</summary>
    public static IReadOnlyList<NetworkAdapter> List()
    {
        var result = new List<NetworkAdapter>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null)
                {
                    continue;
                }

                result.Add(new NetworkAdapter(nic.Id, nic.Name, unicast.Address, unicast.IPv4Mask));
            }
        }

        return result;
    }
}
