using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Networking;

public enum TcpOwnerStatus { Matched, NotFound, Unavailable }
public sealed record TcpOwnerObservation(TcpOwnerStatus Status, int? ProcessId);

[SupportedOSPlatform("windows")]
public static class LoopbackTcpPeer
{
    public static TcpOwnerObservation Observe(IPEndPoint client, IPEndPoint server)
    {
        if (client.Address.AddressFamily != AddressFamily.InterNetwork || server.Address.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.IsLoopback(client.Address) || !IPAddress.IsLoopback(server.Address) || client.Port == 0 || server.Port == 0)
            throw new ArgumentException("An actual IPv4 loopback transport pair is required");
        uint length = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref length, false, 2, 5, 0) != 122)
            return new(TcpOwnerStatus.Unavailable, null);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (length is < 4 or > 4_194_304) return new(TcpOwnerStatus.Unavailable, null);
            var allocated = length;
            var buffer = Marshal.AllocHGlobal(checked((int)allocated));
            try
            {
                var result = GetExtendedTcpTable(buffer, ref length, false, 2, 5, 0);
                if (result == 122) continue;
                if (result != 0) return new(TcpOwnerStatus.Unavailable, null);
                var count = Marshal.ReadInt32(buffer);
                if (count < 0 || 4L + 24L * count > allocated) return new(TcpOwnerStatus.Unavailable, null);
                var owners = new HashSet<int>();
                for (var index = 0; index < count; index++)
                {
                    var row = IntPtr.Add(buffer, 4 + 24 * index);
                    if (Marshal.ReadInt32(row) != 5 || Port(row, 8) != client.Port || Port(row, 16) != server.Port) continue;
                    if (Address(row, 4).Equals(client.Address) && Address(row, 12).Equals(server.Address))
                        owners.Add(Marshal.ReadInt32(row, 20));
                }
                return owners.Count == 1 && owners.Single() > 0 ? new(TcpOwnerStatus.Matched, owners.Single()) :
                    new(owners.Count == 0 ? TcpOwnerStatus.NotFound : TcpOwnerStatus.Unavailable, null);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return new(TcpOwnerStatus.Unavailable, null);
    }

    private static int Port(IntPtr row, int offset) => (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, offset));
    private static IPAddress Address(IntPtr row, int offset) => new(BitConverter.GetBytes(Marshal.ReadInt32(row, offset)));

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool sorted,
        uint addressFamily, uint tableClass, uint reserved);
}
