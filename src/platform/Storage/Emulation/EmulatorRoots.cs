using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using NexusPipeline.Core.Automation;

namespace NexusPipeline.Platform.Storage.Emulation;

internal sealed record EmulatorRoot(Guid Id, string Name, string Path, bool IsDeclared);

[SupportedOSPlatform("windows")]
internal static class EmulatorRoots
{
    internal static IReadOnlyList<EmulatorRoot> Read(IReadOnlyList<AutomationRoot> selected,
        Func<IEnumerable<string>>? registry = null, string? sharedDataRoot = null, IReadOnlyList<string>? hints = null)
    {
        var result = new Dictionary<string, EmulatorRoot>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in selected)
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(root.Path);
            if (!Directory.Exists(path)) throw new IOException("emulator_root_missing");
            result[path] = new(root.Id, root.Name, path, true);
        }
        foreach (var candidate in (registry ?? (() => RegistryRoots(hints ?? [])))().Append(sharedDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)))
        {
            try
            {
                if (!Path.IsPathFullyQualified(candidate)) continue;
                var path = PhysicalPathPolicy.NormalizeWithoutLinks(candidate);
                if (!Directory.Exists(path) || Path.GetDirectoryName(path) is null || result.ContainsKey(path)) continue;
                var identity = PhysicalObjectIdentity.Read(path);
                var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("nexus.emulator.root/1\n" + path + "\n" + identity))[..16]);
                result.Add(path, new(id, Path.GetFileName(path), path, false));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        if (result.Count > 32) throw new IOException("emulator_root_limit");
        return result.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> RegistryRoots(IReadOnlyList<string> hints)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: false);
                    if (uninstall is null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(name, writable: false);
                        var label = name + " " + entry?.GetValue("DisplayName");
                        if (!hints.Any(value => label.Contains(value, StringComparison.OrdinalIgnoreCase))) continue;
                        if (entry?.GetValue("InstallLocation") is string path && !string.IsNullOrWhiteSpace(path)) result.Add(path);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        return result;
    }
}
