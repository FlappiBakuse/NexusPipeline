using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record TargetRetentionDocument(string Format, int Version, ExecutionTargetBinding Binding,
    ProcessIdentity Root, OwnedTargetInventory Inventory, ImmutableArray<BlobReference> NecessaryClosures,
    BlobReference? PrimaryResult, string TargetStage);

[SupportedOSPlatform("windows")]
internal sealed class PreparedRetentionIntent
{
    private readonly TargetJobStore? _owner;
    private readonly PreparedGameRole? _authority;
    private readonly BlobReference _reference;
    private readonly TargetRetentionDocument? _document;
    private readonly PreparedProviderRetention? _provider;
    private readonly PreparedAndroidRetention? _android;
    internal BlobReference Reference => _reference;

    internal PreparedRetentionIntent(TargetJobStore owner, PreparedGameRole authority,
        BlobReference reference, TargetRetentionDocument document)
    { _owner = owner; _authority = authority; _reference = reference; _document = document; }

    internal PreparedRetentionIntent(PreparedProviderRetention provider)
    { _provider = provider; _reference = provider.Reference; }

    internal PreparedRetentionIntent(PreparedAndroidRetention android)
    { _android = android; _reference = android.Reference; }

    internal T Use<T>(OwnedWorkerJob actualJob, OwnedTargetInventory inventory, Func<T> effect) =>
        _android is not null ? _android.Use(actualJob, inventory, effect) : _provider is not null ? _provider.Use(actualJob, inventory, effect) :
            _authority!.Use(actualJob, _document!.Binding, _ => _owner!.UseRetention(_document.Binding, _reference, inventory, effect));
}
