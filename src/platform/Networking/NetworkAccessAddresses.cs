using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NexusPipeline.Platform.Networking;

public sealed record NetworkAccessAddresses(IReadOnlyList<string> Lan, IReadOnlyList<string> Public)
{
    public static NetworkAccessAddresses Read(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    addresses.Add(unicast.Address);
                }
            }
        }
        catch (NetworkInformationException) { }
        return Project(addresses, port);
    }

    public static NetworkAccessAddresses Project(IEnumerable<IPAddress> addresses, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var lan = new SortedSet<string>(StringComparer.Ordinal);
        var publicAddresses = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
            var bytes = address.GetAddressBytes();
            if (bytes[0] is 0 or >= 224 || bytes[0] == 169 && bytes[1] == 254) continue;
            var privateAddress = bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
            if (!privateAddress && !IsPublic(address)) continue;
            var target = privateAddress ? lan : publicAddresses;
            if (target.Count < 32) target.Add(new UriBuilder("http", address.ToString(), port).Uri.GetLeftPart(UriPartial.Authority));
        }
        return new(lan.ToArray(), publicAddresses.ToArray());
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        // IANA special-purpose IPv4 ranges cannot establish a public access address.
        if (b[0] is 0 or 10 or 127 or >= 224 || b[0] == 100 && b[1] is >= 64 and <= 127 ||
            b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
            b[0] == 192 && b[1] == 168 || b[0] == 198 && b[1] is 18 or 19 ||
            b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113 ||
            b[0] == 192 && b[1] == 0 && b[2] == 2 || b[0] == 192 && b[1] == 88 && b[2] == 99)
            return false;
        return b[0] != 192 || b[1] != 0 || b[2] != 0 || b[3] is 9 or 10;
    }
}
