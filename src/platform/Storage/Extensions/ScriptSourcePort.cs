using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Compilation;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ScriptSourcePort : ISelectedSource, IDisposable
{
    private readonly SelectedSourcePort _files;
    private readonly SourceConfiguration[] _configurations;
    private readonly Action _requireCurrent;
    private readonly CancellationToken _lifetime;
    private bool _disposed;

    internal ScriptSourcePort(SelectedProjectSource source, Action requireCurrent, CancellationToken lifetime)
    {
        if (!source.IsScriptAuthority) throw new IOException("project_script_authority_required");
        _requireCurrent = requireCurrent; _lifetime = lifetime;
        _files = new(source, requireCurrent, lifetime);
        var mounts = new List<SourceConfiguration>();
        foreach (var declaration in source.Script.AdapterResources)
        {
            Check();
            var target = source.Script.ConfigurationPaths[declaration.DeclarationIndex];
            if (target.RootId != source.Script.RootId) throw new IOException("project_configuration_root_unbound");
            var captured = source.CaptureResource(target.RelativePath, lifetime);
            if (captured.Archive.Snapshot.Entries.Count > 4096 || captured.Archive.Snapshot.Entries.Sum(item => item.SizeBytes) > 32 * 1024 * 1024)
                throw new IOException("project_configuration_source_limit");
            var contract = AdapterConfigurationPolicy.Contract(source.Script, declaration);
            var snapshot = Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(captured.Archive.Snapshot)));
            mounts.Add(new(declaration.ResourceId, target.RelativePath, source.Script.Revision, contract.Revision,
                contract.DefinitionSha256, snapshot, captured.Archive.Snapshot.Shape == FileObjectKind.File ? SourceEntryKind.File : SourceEntryKind.Directory,
                false, null));
        }
        _configurations = mounts.ToArray();
        source.RequireCurrent();
    }

    public IReadOnlyList<SourceConfiguration> Configurations
    { get { Check(); return Array.AsReadOnly(_configurations); } }
    public ValueTask<SourceDirectory> ListAsync(string relativeDirectory, CancellationToken cancellationToken)
    { Check(); return _files.ListAsync(relativeDirectory, cancellationToken); }
    public ValueTask<SourceFile?> ReadAsync(string relativePath, CancellationToken cancellationToken)
    { Check(); return _files.ReadAsync(relativePath, cancellationToken); }
    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lifetime.ThrowIfCancellationRequested(); _requireCurrent();
    }
    public void Dispose() { _disposed = true; _files.Dispose(); }
}
