using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class UpdateHostImage : IDisposable
{
    private readonly List<SafeFileHandle> _directories = [];
    private FileStream? _image;
    internal string Executable { get; }

    private UpdateHostImage(string root) => Executable = Path.Combine(root, "NexusPipeline.exe");

    internal static string ApplicationRoot()
    {
        var root = Path.TrimEndingDirectorySeparator(OrdinaryPath(AppContext.BaseDirectory));
        var running = OrdinaryPath(Environment.ProcessPath ?? throw Invalid());
        if (!StringComparer.OrdinalIgnoreCase.Equals(running, Path.Combine(root, "NexusPipeline.update.exe")))
            throw new IOException("maintenance_window_wrong_location");
        return root;
    }

    internal static UpdateHostImage Hold(string root)
    {
        var image = new UpdateHostImage(root);
        try
        {
            var parents = new Stack<string>();
            for (var path = root; path is not null; path = Path.GetDirectoryName(path)) parents.Push(path);
            while (parents.TryPop(out var path)) image._directories.Add(Open(path, directory: true));
            var handle = Open(image.Executable, directory: false);
            try { image._image = new FileStream(handle, FileAccess.Read); }
            catch { handle.Dispose(); throw; }
            var expected = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "Nexus.HostSha256").Value;
            if (Convert.ToHexStringLower(SHA256.HashData(image._image)) != expected) throw Invalid();
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    internal SafeProcessHandle Bind(int processId)
    {
        var process = OpenProcess(0x101000, false, processId);
        try
        {
            var path = new StringBuilder(32768);
            uint length = (uint)path.Capacity;
            if (process.IsInvalid || !IsLive(process) || !QueryImage(process, 0, path, ref length) ||
                !StringComparer.OrdinalIgnoreCase.Equals(OrdinaryPath(path.ToString()), Executable) ||
                !OpenProcessToken(process, 8, out var token)) throw Invalid();
            using (token)
            using (var server = new WindowsIdentity(token.DangerousGetHandle()))
            using (var current = WindowsIdentity.GetCurrent())
                if (current.User?.Value is not { } user || user != server.User?.Value || !IsLive(process)) throw Invalid();
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    internal static bool IsLive(SafeProcessHandle process) => WaitForSingleObject(process, 0) == 258;

    private static SafeFileHandle Open(string path, bool directory)
    {
        // Excluding delete sharing pins the validated directory names during process creation.
        var native = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        var handle = OpenFile(native, directory ? 0xa0u : 0x80000000u, directory ? 3u : 1u, 0, 3,
            directory ? 0x02200000u : 0x00200000u, 0);
        if (!handle.IsInvalid && ReadAttributes(handle, 9, out var tag, 8) &&
            (tag.Attributes & FileAttributes.ReparsePoint) == 0 &&
            ((tag.Attributes & FileAttributes.Directory) != 0) == directory) return handle;
        handle.Dispose();
        throw Invalid();
    }

    private static string OrdinaryPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length >= 7 &&
            char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\') path = path[4..];
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal)) throw Invalid();
        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        _image?.Dispose();
        for (var index = _directories.Count - 1; index >= 0; index--) _directories[index].Dispose();
        _directories.Clear();
    }

    private static IOException Invalid() => new("maintenance_window_host_unverified");

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public FileAttributes Attributes; public uint Tag; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle OpenFile(string path, uint access, uint share, nint security, uint mode, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadAttributes(SafeFileHandle handle, int kind, out AttributeTag tag, uint size);

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryImage(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
