using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Platform.Networking;

internal sealed record NetworkAddressCandidate(string Address, bool HasGateway, bool IsVirtual);
internal sealed record RemoteAccessAddresses(string? InternalAddress, string? PublicAddress);

internal static class NetInfo
{
    public static RemoteAccessAddresses GetRemoteAccessAddresses()
        => SelectRemoteAccessAddresses(ReadCandidates());

    public static List<string> ListLanAddresses()
        => ReadCandidates().Select(item => item.Address)
            .Where(address => !IPAddress.IsLoopback(IPAddress.Parse(address)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(address => address, StringComparer.Ordinal).ToList();

    private static List<NetworkAddressCandidate> ReadCandidates()
    {
        var candidates = new List<NetworkAddressCandidate>();
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                IPInterfaceProperties properties = ni.GetIPProperties();
                bool gateway = properties.GatewayAddresses.Any(item => item.Address.AddressFamily == AddressFamily.InterNetwork
                    && !item.Address.Equals(IPAddress.Any));
                string description = ni.Name + " " + ni.Description;
                bool virtualInterface = ni.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
                    || new[] { "virtual", "hyper-v", "vmware", "wintun", "wireguard", "vpn", "tap-", "tun-" }
                        .Any(marker => description.Contains(marker, StringComparison.OrdinalIgnoreCase));
                foreach (UnicastIPAddressInformation ip in properties.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                    {
                        candidates.Add(new(ip.Address.ToString(), gateway, virtualInterface));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[网络] 枚举局域网地址失败：{ex.Message}");
        }
        return candidates;
    }

    internal static RemoteAccessAddresses SelectRemoteAccessAddresses(IEnumerable<NetworkAddressCandidate> candidates)
    {
        var ordered = candidates.Where(item => !item.IsVirtual)
            .Select(item => (Candidate: item, Ip: IPAddress.TryParse(item.Address.Trim(), out IPAddress? ip) ? ip : null))
            .Where(item => item.Ip?.AddressFamily == AddressFamily.InterNetwork)
            .OrderByDescending(item => item.Candidate.HasGateway)
            .ThenBy(item => Convert.ToHexString(item.Ip!.GetAddressBytes()), StringComparer.Ordinal)
            .ToArray();
        return new(
            ordered.FirstOrDefault(item => IsPrivate(item.Ip!)).Ip?.ToString(),
            ordered.FirstOrDefault(item => IsPublic(item.Ip!)).Ip?.ToString());
    }

    private static bool IsPrivate(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
    }

    private static bool IsPublic(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        // 网卡中的共享、链路本地和保留地址不能证明互联网入口，更不能代表 NAT 出口。
        return !IsPrivate(address) && b[0] is not (0 or 127) && b[0] < 224
            && !(b[0] == 100 && b[1] is >= 64 and <= 127)
            && !(b[0] == 169 && b[1] == 254)
            && !(b[0] == 192 && (b[1] == 0 && b[2] is 0 or 2 || b[1] == 88 && b[2] == 99))
            && !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100))
            && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
}
