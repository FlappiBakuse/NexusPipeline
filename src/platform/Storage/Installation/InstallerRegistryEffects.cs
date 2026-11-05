using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal static class InstallerRegistryEffects
{
    internal static void RequireRestorable(string path, InstallerRegistrySnapshot before, InstallerRegistrySnapshot after)
    {
        var current = InstallerMetadataInventory.CaptureRegistry(path);
        if (current.Exists && !before.Exists && !after.Exists) throw InstallationIdentityCodec.Invalid();
        if (!current.Exists && before.Exists && after.Exists) throw InstallationIdentityCodec.Invalid();
        var original = before.Values.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var installed = after.Values.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var value in current.Values)
            if (!Equal(value, original.GetValueOrDefault(value.Name)) && !Equal(value, installed.GetValueOrDefault(value.Name)))
                throw InstallationIdentityCodec.Invalid();
        foreach (var name in original.Keys.Intersect(installed.Keys, StringComparer.OrdinalIgnoreCase))
            if (!current.Values.Any(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw InstallationIdentityCodec.Invalid();
    }

    internal static void Restore(string path, InstallerRegistrySnapshot before, InstallerRegistrySnapshot after)
    {
        RequireRestorable(path, before, after);
        var current = InstallerMetadataInventory.CaptureRegistry(path);
        if (Equal(current, before)) return;
        if (!before.Exists)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(path, writable: true))
                if (key is not null)
                    foreach (var value in current.Values)
                    {
                        RequireRestorable(path, before, after);
                        key.DeleteValue(value.Name, throwOnMissingValue: false);
                    }
            RequireRestorable(path, before, after);
            using (var key = Registry.CurrentUser.OpenSubKey(path))
                if (key is not null && (key.GetValueNames().Length != 0 || key.GetSubKeyNames().Length != 0))
                    throw InstallationIdentityCodec.Invalid();
            Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
        else
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            var originals = before.Values.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var value in current.Values.Where(value => !originals.ContainsKey(value.Name)))
            {
                RequireRestorable(path, before, after);
                key.DeleteValue(value.Name, throwOnMissingValue: false);
            }
            foreach (var value in before.Values)
            {
                RequireRestorable(path, before, after);
                key.SetValue(value.Name, Payload(value), value.Kind);
            }
        }
        if (!Equal(InstallerMetadataInventory.CaptureRegistry(path), before)) throw InstallationIdentityCodec.Invalid();
    }

    private static object Payload(InstallerRegistryValue value) => value.Kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => value.Text!,
        RegistryValueKind.MultiString => value.Lines!, RegistryValueKind.Binary => Convert.FromBase64String(value.Binary!),
        RegistryValueKind.DWord => checked((int)value.Number!.Value), RegistryValueKind.QWord => value.Number!.Value,
        _ => throw InstallationIdentityCodec.Invalid()
    };

    private static bool Equal<T>(T left, T right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
