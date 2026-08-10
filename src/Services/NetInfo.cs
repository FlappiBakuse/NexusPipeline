using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>本机网络信息：远程访问时枚举可供局域网设备访问的 IPv4 地址。</summary>
internal static class NetInfo
{
    /// <summary>枚举本机非回环、处于启用状态的 IPv4 单播地址（含虚拟网卡，用户自行辨认）。</summary>
    public static List<string> ListLanAddresses()
    {
        var result = new List<string>();
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
                foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                    {
                        result.Add(ip.Address.ToString());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[网络] 枚举局域网地址失败：{ex.Message}");
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(addr => addr, StringComparer.Ordinal).ToList();
    }
}
