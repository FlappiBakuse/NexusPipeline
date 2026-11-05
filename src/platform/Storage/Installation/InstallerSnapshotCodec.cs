using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal static class InstallerSnapshotCodec
{
    private sealed record Envelope(string Format, int Version, InstallerMetadataSnapshot Snapshot);
    private static readonly string[] Roles = ["desktop", "group", "manager", "uninstall"];
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };
    internal const int MaximumBytes = 131072;

    internal static byte[] Encode(InstallerMetadataSnapshot snapshot)
    {
        Validate(snapshot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope("nexus.installer.snapshot", 1, snapshot), Options);
        if (bytes.Length > MaximumBytes) throw InstallationIdentityCodec.Invalid();
        return bytes;
    }

    internal static InstallerMetadataSnapshot Decode(ReadOnlyMemory<byte> bytes, string expectedRoot)
    {
        using var document = StrictJson.Parse(bytes, MaximumBytes, MaximumBytes);
        var value = document.Deserialize<Envelope>(Options) ?? throw InstallationIdentityCodec.Invalid();
        if (value.Format != "nexus.installer.snapshot" || value.Version != 1) throw InstallationIdentityCodec.Invalid();
        Validate(value.Snapshot);
        if (!StringComparer.OrdinalIgnoreCase.Equals(value.Snapshot.Root, PhysicalPathPolicy.NormalizeSyntax(expectedRoot)))
            throw InstallationIdentityCodec.Invalid();
        return value.Snapshot;
    }

    internal static void Validate(InstallerMetadataSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Root is null || snapshot.Root != PhysicalPathPolicy.NormalizeSyntax(snapshot.Root) ||
            snapshot.Root.StartsWith("\\\\", StringComparison.Ordinal) || Path.GetDirectoryName(snapshot.Root) is null ||
            snapshot.RootIdentity is not null && !InstallationIdentityCodec.Physical(snapshot.RootIdentity) ||
            snapshot.Files is null || snapshot.Files.Length > 7 || snapshot.Directories is null || snapshot.Directories.Length != Roles.Length ||
            snapshot.Directories.Any(directory => directory is null || directory.Identity is not null && !InstallationIdentityCodec.Physical(directory.Identity)) ||
            !snapshot.Directories.Select(directory => directory.Role).SequenceEqual(Roles)) throw InstallationIdentityCodec.Invalid();
        ValidateRegistry(snapshot.Uninstall); ValidateRegistry(snapshot.Ownership);
        if (snapshot.RootIdentity is null && snapshot.Directories.Single(directory => directory.Role == "uninstall").Identity is not null)
            throw InstallationIdentityCodec.Invalid();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot.Files)
            if (file is null || !InstallerMetadataInventory.KnownFile(file.Role, file.Name) || !keys.Add(file.Role + "/" + file.Name) ||
                !InstallationIdentityCodec.Physical(file.Identity) || !InstallationIdentityCodec.Physical(file.ParentIdentity) ||
                file.ParentIdentity != snapshot.Directories.Single(directory => directory.Role == file.Role).Identity ||
                file.SizeBytes is < 1 or > 268435456 || !InstallationIdentityCodec.Digest(file.Sha256)) throw InstallationIdentityCodec.Invalid();
        var ordered = snapshot.Files.OrderBy(file => file.Role, StringComparer.Ordinal).ThenBy(file => file.Name, StringComparer.Ordinal);
        if (!snapshot.Files.SequenceEqual(ordered) || JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > MaximumBytes - 128)
            throw InstallationIdentityCodec.Invalid();
    }

    private static void ValidateRegistry(InstallerRegistrySnapshot snapshot)
    {
        if (snapshot is null || snapshot.Values is null || snapshot.Values.Length > 64 || !snapshot.Exists && snapshot.Values.Length != 0)
            throw InstallationIdentityCodec.Invalid();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in snapshot.Values)
        {
            if (value is null || value.Name is not { Length: >= 1 and <= 128 } || value.Name.Any(char.IsControl) || !names.Add(value.Name))
                throw InstallationIdentityCodec.Invalid();
            var valid = value.Kind switch
            {
                RegistryValueKind.String or RegistryValueKind.ExpandString => value.Text is { Length: <= 32768 } &&
                    value.Lines is null && value.Binary is null && value.Number is null,
                RegistryValueKind.MultiString => value.Text is null && value.Binary is null && value.Number is null &&
                    value.Lines is { Length: <= 64 } && value.Lines.All(line => line is { Length: <= 8192 }),
                RegistryValueKind.Binary => value.Text is null && value.Lines is null && value.Number is null && Binary(value.Binary),
                RegistryValueKind.DWord => value.Text is null && value.Lines is null && value.Binary is null && value.Number is >= int.MinValue and <= int.MaxValue,
                RegistryValueKind.QWord => value.Text is null && value.Lines is null && value.Binary is null && value.Number is not null,
                _ => false
            };
            if (!valid) throw InstallationIdentityCodec.Invalid();
        }
        if (!snapshot.Values.Select(value => value.Name).SequenceEqual(snapshot.Values.Select(value => value.Name).Order(StringComparer.Ordinal)))
            throw InstallationIdentityCodec.Invalid();
    }

    private static bool Binary(string? value)
    {
        if (value is null || value.Length > 87384) return false;
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length <= 65536 && Convert.ToBase64String(bytes) == value;
        }
        catch (FormatException) { return false; }
    }
}
