using System.Collections.Immutable;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class ConfigurationTaskTransformation(ImmutableBlobStore blobs)
{
    internal Action? DocumentRead { get; init; }
    public ImmutableArray<ConfigurationCapturedTasks> Capture(Guid configurationOperationId, ConfigurationRevision source,
        ImmutableArray<RunTaskRule> rules, CancellationToken cancellationToken = default)
    {
        source = FreezeSource(configurationOperationId, source);
        rules = RunTaskPolicy.Freeze(rules);
        return rules.Select(rule =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = RequireFile(source.Tree, rule.RelativePath);
            return Read(configurationOperationId, source, entry, rule, document =>
                new ConfigurationCapturedTasks(rule, TaskSelectionPolicy.Capture(document, rule.Declaration)));
        }).ToImmutableArray();
    }

    public void ValidateInitial(Guid configurationOperationId, ConfigurationRevision source,
        ImmutableArray<ConfigurationCapturedTasks> captured, CancellationToken cancellationToken = default)
    {
        source = FreezeSource(configurationOperationId, source);
        var ordered = FreezeCaptured(configurationOperationId, source, captured);
        var actual = Capture(configurationOperationId, source, ordered.Select(item => item.Rule).ToImmutableArray(), cancellationToken);
        for (var index = 0; index < ordered.Length; index++)
            if (ordered[index].Initial.Values.Any(pair => actual[index].Initial.Values[pair.Key] != pair.Value))
                throw new ConfigurationException("configuration_run_initial_tasks_changed");
    }

    public FileTreeSnapshot Select(Guid configurationOperationId, ConfigurationRevision source,
        ImmutableArray<ConfigurationCapturedTasks> captured, CancellationToken cancellationToken = default, FileTreeSnapshot? inputTree = null)
    {
        source = FreezeSource(configurationOperationId, source);
        return Transform(configurationOperationId, source, captured, inputTree ?? source.Tree, restore: false, persist: true, cancellationToken);
    }

    public FileTreeSnapshot Restore(Guid configurationOperationId, ConfigurationRevision source,
        ImmutableArray<ConfigurationCapturedTasks> captured, FileTreeSnapshot progress, CancellationToken cancellationToken = default) =>
        Transform(configurationOperationId, source, captured, progress, restore: true, persist: true, cancellationToken);

    public void ValidateSelected(Guid configurationOperationId, ConfigurationRevision source,
        ImmutableArray<ConfigurationCapturedTasks> captured, FileTreeSnapshot candidate, CancellationToken cancellationToken = default, FileTreeSnapshot? inputTree = null)
    {
        source = FreezeSource(configurationOperationId, source);
        candidate = FileTreePolicy.Freeze(candidate);
        var expected = Transform(configurationOperationId, source, captured, inputTree ?? source.Tree, restore: false, persist: false, cancellationToken);
        if (candidate.Shape != expected.Shape || !candidate.Entries.SequenceEqual(expected.Entries))
            throw new ConfigurationException("configuration_run_selected_tasks_changed");
    }

    private FileTreeSnapshot Transform(Guid operationId, ConfigurationRevision source, ImmutableArray<ConfigurationCapturedTasks> captured,
        FileTreeSnapshot input, bool restore, bool persist, CancellationToken cancellationToken)
    {
        source = FreezeSource(operationId, source);
        var ordered = FreezeCaptured(operationId, source, captured);
        var tree = FileTreePolicy.Freeze(input);
        var replacements = new Dictionary<string, FileTreeEntry>(StringComparer.Ordinal);
        foreach (var item in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = RequireFile(tree, item.Rule.RelativePath);
            var initial = RequireFile(source.Tree, item.Rule.RelativePath);
            var output = Read(operationId, source, initial, item.Rule, document =>
            {
                var actual = TaskSelectionPolicy.Capture(document, item.Rule.Declaration);
                if (item.Initial.Values.Any(pair => actual.Values[pair.Key] != pair.Value))
                    throw new ConfigurationException("configuration_run_initial_tasks_changed");
                byte[] Apply(ConfigurationDocument current) => current.Apply(restore
                    ? TaskSelectionPolicy.Restore(current, item.Initial)
                    : TaskSelectionPolicy.Select(current, item.Initial, RunTaskPolicy.Choices(item.Rule)));
                return initial.Sha256 == entry.Sha256 && initial.SizeBytes == entry.SizeBytes
                    ? Apply(document) : Read(operationId, source, entry, item.Rule, Apply);
            });
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var digest = Convert.ToHexStringLower(SHA256.HashData(output));
                if (persist) blobs.PersistContent(output, cancellationToken);
                replacements.Add(entry.RelativePath, entry with { Sha256 = digest, SizeBytes = output.Length });
            }
            finally { CryptographicOperations.ZeroMemory(output); }
        }
        return FileTreePolicy.Freeze(tree with
        {
            Entries = Array.AsReadOnly(tree.Entries.Select(entry => replacements.GetValueOrDefault(entry.RelativePath, entry)).ToArray())
        });
    }

    private T Read<T>(Guid operationId, ConfigurationRevision source, FileTreeEntry entry, RunTaskRule rule,
        Func<ConfigurationDocument, T> action)
    {
        if (entry.SizeBytes > StrictJson.MaximumBytes) throw new ConfigurationException("configuration_document_limit");
        using var input = blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
        DocumentRead?.Invoke();
        var bytes = new byte[checked((int)entry.SizeBytes)];
        try
        {
            input.ReadExactly(bytes);
            var binding = new ConfigurationPatchBinding(operationId, source.Address.ResourceId, source.Revision, entry.Sha256!);
            using var document = new ConfigurationDocument(binding, rule.Syntax, rule.Access, bytes);
            return action(document);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static ImmutableArray<ConfigurationCapturedTasks> FreezeCaptured(Guid operationId, ConfigurationRevision source,
        ImmutableArray<ConfigurationCapturedTasks> captured)
    {
        if (captured.IsDefault || captured.Any(item => item is null))
            throw new ConfigurationException("configuration_run_initial_tasks_invalid");
        var rules = RunTaskPolicy.Freeze(captured.Select(item => item.Rule).ToImmutableArray());
        var byPath = captured.ToDictionary(item => item.Rule.RelativePath, StringComparer.Ordinal);
        return rules.Select(rule =>
        {
            var item = byPath[rule.RelativePath] with { Rule = rule };
            RunTaskPolicy.RequireInitial(item, operationId, source);
            return item with { Initial = item.Initial with { Declaration = rule.Declaration } };
        }).ToImmutableArray();
    }

    private static ConfigurationRevision FreezeSource(Guid operationId, ConfigurationRevision source)
    {
        if (operationId == Guid.Empty || source is null || source.Address is null || source.UserId == Guid.Empty || source.ScriptId == Guid.Empty ||
            source.Revision is < 1 or > 9007199254740991)
            throw new ConfigurationException("configuration_run_source_invalid");
        ConfigurationRevisionPolicy.ValidateContract(source.Contract);
        return source with { Tree = FileTreePolicy.Freeze(source.Tree) };
    }

    private static FileTreeEntry RequireFile(FileTreeSnapshot tree, string relativePath) =>
        tree.Entries.SingleOrDefault(entry => entry.RelativePath == relativePath && entry.Kind == FileObjectKind.File)
            ?? throw new ConfigurationException("configuration_run_task_resource_missing");
}
