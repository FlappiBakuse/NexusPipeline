using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Security;

[SupportedOSPlatform("windows")]
internal static class WindowsDataProtection
{
    internal static byte[] Transform(byte[] input, byte[] entropy, bool protect, int maximumOutputBytes = 1048576)
    {
        if (maximumOutputBytes is < 1 or > 5 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        var data = Allocate(input);
        var context = default(DataBlob);
        var output = default(DataBlob);
        try
        {
            context = Allocate(entropy);
            var success = protect
                ? CryptProtectData(ref data, nint.Zero, ref context, nint.Zero, nint.Zero, 1, out output)
                : CryptUnprotectData(ref data, nint.Zero, ref context, nint.Zero, nint.Zero, 1, out output);
            if (!success) throw new IOException("secret_unavailable", new Win32Exception(Marshal.GetLastPInvokeError()));
            if (output.Data == 0 || output.Size == 0 || output.Size > maximumOutputBytes) throw new IOException("secret_protection_output_invalid");
            var result = new byte[checked((int)output.Size)];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ClearAndFree(data, local: false);
            ClearAndFree(context, local: false);
            ClearAndFree(output, local: true);
        }
    }

    private static DataBlob Allocate(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, pointer, data.Length);
            return new DataBlob { Size = checked((uint)data.Length), Data = pointer };
        }
        catch { Marshal.FreeHGlobal(pointer); throw; }
    }

    private static void ClearAndFree(DataBlob blob, bool local)
    {
        if (blob.Data == 0) return;
        try
        {
            var zeros = new byte[4096];
            for (long offset = 0; offset < blob.Size; offset += zeros.Length)
                Marshal.Copy(zeros, 0, blob.Data + checked((int)offset), checked((int)Math.Min(zeros.Length, blob.Size - offset)));
        }
        finally
        {
            if (local) LocalFree(blob.Data);
            else Marshal.FreeHGlobal(blob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { internal uint Size; internal nint Data; }
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, nint description, ref DataBlob entropy,
        nint reserved, nint prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, nint description, ref DataBlob entropy,
        nint reserved, nint prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
