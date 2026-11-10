using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal static class InstallerShortcut
{
    internal static void Require(string path, string root)
    {
        object instance = new ShellLink();
        try
        {
            ((IPersistFile)instance).Load(path, 0);
            var link = (IShellLink)instance;
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            // Raw paths keep ownership checks independent of Shell target repair.
            var status = link.GetPath(target, target.Capacity, IntPtr.Zero, 4);
            if (status < 0) Marshal.ThrowExceptionForHR(status);
            link.GetArguments(arguments, arguments.Capacity);
            string[] allowed = Path.GetFileName(path) == InstallationAnchor.UninstallShortcut
                ? [InstallationAnchor.PublicUninstaller] : [ApplicationTree.HostEntry, ApplicationTree.LauncherEntry];
            if (status != 0 || target.Length == 0 || arguments.Length != 0 ||
                !allowed.Any(entry => StringComparer.OrdinalIgnoreCase.Equals(
                    PhysicalPathPolicy.NormalizeWithoutLinks(target.ToString()), Path.Combine(root, entry))))
                throw InstallationIdentityCodec.Invalid();
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr data, uint flags);
        void GetIdList(out IntPtr list);
        void SetIdList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
    }
}
