using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Processes;

internal enum MaintenanceStartupRole { HelperBootstrap, DesktopRoot, HostRoot }

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceStartupEnvironment : IDisposable
{
    internal IntPtr Pointer { get; private set; }

    private MaintenanceStartupEnvironment(string block) => Pointer = Marshal.StringToHGlobalUni(block);

    internal static MaintenanceStartupEnvironment Freeze(MaintenanceStartupRole role)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || entry.Value is not string value || name.Length == 0 || name[0] == '=') continue;
            if (name.Contains('\0') || name.Contains('=') || value.Contains('\0')) throw Invalid();
            if (IsRuntimeOverride(name) || role == MaintenanceStartupRole.DesktopRoot &&
                (name.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("CHROME_CRASHPAD_", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("BREAKPAD_DUMP_LOCATION", StringComparison.OrdinalIgnoreCase))) continue;
            values.Add(name, value);
        }
        values["DOTNET_EnableDiagnostics"] = "0";
        values["COMPlus_EnableDiagnostics"] = "0";
        values["CORECLR_ENABLE_PROFILING"] = "0";
        values["COR_ENABLE_PROFILING"] = "0";
        values["DOTNET_ENVIRONMENT"] = "Production";
        values["ASPNETCORE_ENVIRONMENT"] = "Production";
        if (ActualFrameworkRoot() is { } framework)
        {
            values["DOTNET_ROOT"] = framework;
            values["DOTNET_ROOT_X64"] = framework;
            values["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        }
        var block = string.Join('\0', values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Key + "=" + item.Value)) + "\0\0";
        if (block.Length > 1_048_576) throw Invalid();
        return new(block);
    }

    private static bool IsRuntimeOverride(string name) => name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("__COMPAT_LAYER", StringComparison.OrdinalIgnoreCase);

    private static string? ActualFrameworkRoot()
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        if (runtime.Parent?.Name != "Microsoft.NETCore.App" || runtime.Parent.Parent?.Name != "shared") return null;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("maintenance_runtime_architecture_unsupported");
        return PhysicalPathPolicy.NormalizeWithoutLinks(runtime.Parent.Parent.Parent?.FullName ?? throw Invalid());
    }

    public void Dispose()
    {
        if (Pointer == IntPtr.Zero) return;
        Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero;
    }
    private static IOException Invalid() => new("maintenance_startup_environment_invalid");
}
