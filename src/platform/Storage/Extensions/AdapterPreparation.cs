using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class AdapterPreparation(StateDatabase database, ExtensionActivator activator, SecretAccessPolicy secrets,
    ProjectCompilationService compilation, ImmutableBlobStore blobs)
{
    internal ValueTask<TaskAdapter> ExecuteAsync(ResolvedAdapterSelection source, string phase, CancellationToken cancellation,
        IReadOnlyList<TaskDefinitionsResource>? candidateResources = null)
    {
        if (phase != "configuration" && !source.Selection.ConfigurationIntents.IsDefaultOrEmpty)
            throw new IOException("adapter_configuration_intent_phase_invalid");
        var identity = new TaskMessageIdentity(source.Selection.ExtensionId, source.Selection.OperationId, phase, null, null);
        var request = new TaskAdapter
        {
            Format = "nexus.task.adapter", Version = 1, Direction = "request", Phase = phase,
            ExtensionId = identity.ExtensionId, OperationId = identity.OperationId.ToString("D"), Resources = candidateResources ?? source.Resources,
            ConfigurationIntents = phase == "configuration" ? Intents(source) : null
        };
        return activator.RequirePublishedRuntime(identity.ExtensionId).InvokeAdapterAsync(source.Root, source.RequireCurrent,
            async (executor, requireCurrent) =>
            {
                void Check() { source.RequireCurrent(); requireCurrent(); }
                using var authorization = secrets.EnterSelectedBinding(source.Root, source.Selection.BindingId, identity.ExtensionId, Check);
                if (!executor.UsesCompilation)
                {
                    var result = executor.Execute(JsonSerializer.SerializeToUtf8Bytes(request), identity, cancellation, null, source.ReadSources(executor, cancellation));
                    Check(); return result;
                }
                return await compilation.UseSavedAsync(source.Selection.BindingId, source.Selection.ExpectedBindingRevision,
                    identity.ExtensionId, source.RequireLive, (snapshot, current, live) =>
                    {
                        using var input = new CompiledAdapterInput(blobs, snapshot, live);
                        source.BindCompilation(snapshot.Document.Sha256);
                        var result = executor.Execute(JsonSerializer.SerializeToUtf8Bytes(request with { Compiled = input.Wire }), identity, cancellation, input);
                        current(); Check(); return ValueTask.FromResult(result);
                    }, cancellation, source.Root).ConfigureAwait(false);
            });
    }

    private IReadOnlyList<ConfigPatch> Intents(ResolvedAdapterSelection source)
    {
        var intents = source.Selection.ConfigurationIntents;
        if (intents.IsDefaultOrEmpty) return [];
        var definitions = new AutomationDefinitionStore(database).Read();
        var binding = definitions.Bindings.Single(item => item.Id == source.Selection.BindingId);
        var script = definitions.Scripts.Single(item => item.Id == binding.ScriptId);
        var result = new List<ConfigPatch>();
        foreach (var intent in intents)
        {
            var resource = source.Resources.Single(item => item.ResourceId == intent.Binding.ResourceId);
            if (intent.Binding.SourceSha256 != resource.Sha256 || intent.Binding.Revision != resource.Revision)
                throw new IOException("adapter_configuration_intent_changed");
            var declaration = script.AdapterResources.Single(item => item.ResourceId == resource.ResourceId);
            var bytes = Encoding.UTF8.GetBytes(resource.Content);
            try
            {
                using var document = new ConfigurationDocument(intent.Binding, declaration.Syntax, declaration.Access, bytes);
                var candidate = document.Apply(intent);
                CryptographicOperations.ZeroMemory(candidate);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            result.Add(new()
            {
                Format = "nexus.config.patch", Version = 1, OperationId = intent.Binding.OperationId.ToString("D"),
                ResourceId = intent.Binding.ResourceId, SourceSha256 = intent.Binding.SourceSha256,
                ExpectedRevision = intent.Binding.Revision,
                Edits = intent.Edits.Select(edit => new ConfigPatchEditsItem
                {
                    Selector = ConfigurationSelectorProtocol.Write(edit.Selector, false), Expected = edit.Expected, Value = edit.Value,
                    ExpectedExists = edit.ExpectedExists
                }).ToArray()
            });
        }
        source.RequireCurrent(); return result;
    }
}
