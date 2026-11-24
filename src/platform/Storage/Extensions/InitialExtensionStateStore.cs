using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record InitialExtensionFile(string Name, long SizeBytes, string Sha256, PhysicalObjectIdentity Identity);
internal sealed record InitialExtensionState(string Format, int Version, Guid InstanceId, string SourcePath,
    PhysicalObjectIdentity SourceIdentity, InitialExtensionFile[] Files, BlobReference Pointer, BlobReference Catalog,
    BlobReference Signature, string[] ExtensionIds, string[] EnableIds,
    bool PreferencesPrepared, bool Complete, Guid CatalogOperation, Guid OperationId, ExtensionApplyRequest? Request,
    Guid PreferenceOperation, HostSettingsChange? PreferenceChange);

internal sealed class InitialExtensionStateStore(RuntimeInstance instance, InstanceLease? lease)
{
    private readonly string _path = Path.Combine(instance.RuntimeRoot, "initial-extensions.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal InitialExtensionState? Read()
    {
        lease?.VerifyHeld(instance);
        PhysicalPathPolicy.NormalizeWithoutLinks(_path);
        if (!File.Exists(_path)) return null;
        using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 1048576) throw Invalid();
        using var strict = StrictJson.Parse(ReadBytes(input));
        var value = strict.RootElement.Deserialize<InitialExtensionState>(Json) ?? throw Invalid();
        Validate(value); return value;
    }

    internal InitialExtensionState Save(InitialExtensionState value)
    {
        Validate(value); (lease ?? throw Invalid()).VerifyHeld(instance);
        var pending = PhysicalPathPolicy.NormalizeWithoutLinks(_path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(output, value, Json); output.Flush(flushToDisk: true); }
            PhysicalPathPolicy.NormalizeWithoutLinks(_path);
            File.Move(pending, _path, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
        return value;
    }

    internal static byte[] ReadBytes(Stream input)
    {
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }

    internal static IReadOnlyList<BlobReference> PendingContent(RuntimeInstance instance)
    {
        var state = new InitialExtensionStateStore(instance, null).Read();
        return state is null || state.Complete ? [] : state.Files.Where(file => file.Name.EndsWith(".zip", StringComparison.Ordinal))
            .Select(file => new BlobReference("blobs/content/" + file.Sha256 + ".bin", file.SizeBytes, file.Sha256)).ToArray();
    }

    private void Validate(InitialExtensionState value)
    {
        if (value.Format != "nexus.extensions.initialization" || value.Version != 1 || value.InstanceId != instance.InstanceId ||
            value.ExtensionIds is not { Length: >= 1 and <= 64 } || value.Files is not { Length: >= 4 and <= 68 } ||
            value.EnableIds is null || value.ExtensionIds.Distinct(StringComparer.Ordinal).Count() != value.ExtensionIds.Length ||
            value.EnableIds.Any(id => !value.ExtensionIds.Contains(id, StringComparer.Ordinal)) ||
            value.Files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Files.Length ||
            value.SourceIdentity is null || value.CatalogOperation == Guid.Empty || value.SourcePath != PhysicalPathPolicy.NormalizeWithoutLinks(value.SourcePath) ||
            value.SourcePath == instance.RuntimeRoot || value.SourcePath.StartsWith(instance.RuntimeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            value.Request is not null && (value.OperationId == Guid.Empty || !value.ExtensionIds.Contains(value.Request.ExtensionId)) ||
            value.PreferenceChange is not null && value.PreferenceOperation == Guid.Empty) throw Invalid();
        foreach (var id in value.ExtensionIds) NexusPipeline.Core.Extensions.ExtensionIdentifiers.Require(id);
        foreach (var file in value.Files)
        {
            PhysicalPathPolicy.ValidateRelativePath(file.Name);
            if (file.Name.Contains('/') || file.Name.Contains('\\') || file.Identity is null ||
                file.SizeBytes is < 1 or > 268435456 || file.Sha256 is not { Length: 64 } ||
                file.Sha256.Any(c => !char.IsAsciiHexDigitLower(c))) throw Invalid();
        }
    }

    internal static IOException Invalid() => new("initial_extensions_state_invalid");
}
