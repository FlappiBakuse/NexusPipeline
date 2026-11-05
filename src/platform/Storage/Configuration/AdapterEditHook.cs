using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed class AdapterEditHook(ExtensionActivator activator, ResourceLeaseManager resources,
    RuntimeInstance instance, ContractReader contracts)
{
    internal (ConfigurationEditAdapter? Rule, bool Exclusive) Bind(string extensionId, string resourceId, string relativePath, ConfigurationDocumentAccess access, IReadOnlyList<string> arguments)
    {
        using var root = Acquire(extensionId);
        return activator.RequirePublishedRuntime(extensionId).InvokeAdapterAsync(root, () => { }, (executor, current) =>
        {
            var original = executor.ReadInputs();
            var manifest = new ExtensionManifestAdmission(contracts).ReadCurrent(original.Manifest, extensionId);
            if ((manifest.Specialized?.EditorForbiddenArguments ?? []).Any(forbidden =>
                arguments.Any(value => value.Split('=')[0] == forbidden)))
                throw new ConfigurationException("configuration_editor_automatic_arguments_forbidden");
            var rule = manifest.Specialized?.EditorLifecycle;
            if (rule is null || rule.ResourceId != resourceId) return ValueTask.FromResult<(ConfigurationEditAdapter?, bool)>((null, manifest.Specialized?.ExclusiveProgram == true));
            var selector = new ConfigurationSelector([new ConfigurationProperty(rule.Field)]);
            bool Covers(ConfigurationSelector value, bool descendants) => value.Steps.SequenceEqual(selector.Steps) || descendants && value.Steps.IsEmpty;
            if (!access.Read.Any(grant => Covers(grant.Selector, grant.Descendants)) ||
                !access.Write.Any(grant => Covers(grant.Selector, grant.Descendants)) || access.Protected.Any(value => Covers(value, true)))
                throw new ConfigurationException("configuration_edit_adapter_field_not_granted");
            current();
            return ValueTask.FromResult<(ConfigurationEditAdapter?, bool)>((new(extensionId, Hash(original.Manifest), Hash(original.Script),
                resourceId, relativePath, rule.Field, rule.PrepareValue, rule.CommitValue), manifest.Specialized?.ExclusiveProgram == true));
        }).GetAwaiter().GetResult();
    }

    internal FileTreeSnapshot Apply(Guid operationId, ConfigurationAddress address, long revision,
        FileTreeSnapshot tree, ConfigurationEditAdapter rule, string purpose)
    {
        var blobs = new ImmutableBlobStore(instance, contracts);
        var prepared = Transform(blobs, operationId, address, revision, tree, rule, purpose);
        try
        {
            using var root = Acquire(rule.ExtensionId);
            activator.RequirePublishedRuntime(rule.ExtensionId).InvokeAdapterAsync(root, () => { }, (executor, current) =>
            {
                var original = executor.ReadInputs();
                if (Hash(original.Manifest) != rule.ManifestSha256 || Hash(original.Script) != rule.ScriptSha256)
                    throw new ConfigurationException("configuration_edit_adapter_changed");
                var entry = tree.Entries.Single(item => item.RelativePath == rule.RelativePath && item.Kind == FileObjectKind.File);
                using var source = blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
                var bytes = new byte[checked((int)source.Length)]; source.ReadExactly(bytes);
                try
                {
                    var request = new TaskAdapter
                    {
                        Format = "nexus.task.adapter", Version = 1, Direction = "request", Phase = "configuration", Purpose = purpose,
                        ExtensionId = rule.ExtensionId, OperationId = operationId.ToString("D"),
                        Resources = [TaskResourceValue.Create(operationId, rule.ResourceId, checked(revision + 1), entry.Sha256!, bytes, ConfigurationSyntax.Json)
                            with { RelativePath = rule.RelativePath }]
                    };
                    var identity = new TaskMessageIdentity(rule.ExtensionId, operationId, "configuration", null, null);
                    var response = executor.Execute(JsonSerializer.SerializeToUtf8Bytes(request), identity, default);
                    if (response.Diagnostics?.Any(item => item.Severity == "blocking") == true || response.ConfigPatches is not { Count: 1 })
                        throw new ConfigurationException("configuration_edit_adapter_patch_required");
                    var actual = new ConfigurationPatchReader(contracts).Read(JsonSerializer.SerializeToUtf8Bytes(response.ConfigPatches[0]));
                    if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(actual), JsonSerializer.SerializeToElement(prepared.Patch)))
                        throw new ConfigurationException("configuration_edit_adapter_patch_exceeds_declaration");
                    current(); return ValueTask.FromResult(true);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }).GetAwaiter().GetResult();
            blobs.PersistContent(prepared.Content, default);
            return prepared.Tree;
        }
        finally { CryptographicOperations.ZeroMemory(prepared.Content); }
    }

    internal static (FileTreeSnapshot Tree, byte[] Content, ConfigurationPatchRequest Patch) Transform(ImmutableBlobStore blobs,
        Guid operationId, ConfigurationAddress address, long revision, FileTreeSnapshot tree, ConfigurationEditAdapter rule, string purpose)
    {
        if (purpose is not ("editPrepare" or "editCommit") || address.ResourceId != rule.ResourceId || revision is < 0 or >= 9007199254740991)
            throw new ConfigurationException("configuration_edit_adapter_purpose_invalid");
        var entry = tree.Entries.SingleOrDefault(item => item.RelativePath == rule.RelativePath && item.Kind == FileObjectKind.File)
            ?? throw new ConfigurationException("configuration_edit_adapter_resource_missing");
        if (entry.SizeBytes > 1048576) throw new ConfigurationException("configuration_edit_adapter_resource_limit");
        using var source = blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
        var bytes = new byte[checked((int)source.Length)]; source.ReadExactly(bytes);
        try
        {
            using var parsed = StrictJson.Parse(bytes);
            var exists = parsed.RootElement.TryGetProperty(rule.Field, out var previous);
            var selector = new ConfigurationSelector([new ConfigurationProperty(rule.Field)]);
            // The working candidate is the next revision; the edit transaction retains the saved revision for publication CAS.
            var patch = new ConfigurationPatchRequest(new(operationId, address.ResourceId, checked(revision + 1), entry.Sha256!),
                [new(selector, exists ? previous.Clone() : JsonSerializer.SerializeToElement<object?>(null),
                    JsonSerializer.SerializeToElement(purpose == "editPrepare" ? rule.PrepareValue : rule.CommitValue), exists)]);
            using var document = new ConfigurationDocument(patch.Binding, ConfigurationSyntax.Json,
                new([new(selector, false)], [new(selector, false)], []), bytes);
            var content = document.Apply(patch);
            var result = tree with { Entries = Array.AsReadOnly(tree.Entries.Select(item => item == entry
                ? item with { Sha256 = Hash(content), SizeBytes = content.Length } : item).ToArray()) };
            return (result, content, patch);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private ResourceOperationLease Acquire(string extensionId) => resources.TryAcquire(Guid.NewGuid(),
        [new("extension:" + extensionId, ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference)]).Lease
        ?? throw new ConfigurationException("configuration_edit_adapter_unavailable");
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
