using System.Collections.Immutable;

namespace NexusPipeline.Core.Extensions;

public sealed record ExtensionCatalogView(ExtensionCatalogIdentity Identity, DateTimeOffset PublishedAt,
    ImmutableArray<ExtensionCatalogEntry> Entries);
public sealed record ExtensionCatalogDetails(ExtensionCatalogIdentity Identity, string ExtensionId,
    string PackageVersion, System.Text.Json.JsonElement Manifest, IReadOnlyDictionary<string, string> Documents);
public sealed record ExtensionApplyCommand(ExtensionApplyAction Action, string ExtensionId,
    long ExpectedInstalledRevision, long ExpectedSettingsRevision,
    ExtensionCatalogIdentity? Catalog, ExtensionCatalogEntry? Candidate);
public sealed record ExtensionApplyAccepted(Guid OperationId, Guid JournalId, string Status);

public interface IExtensionPackages
{
    bool HasReleaseTrust { get; }
    ExtensionCatalogView? ReadCached(ExtensionStoreChannel channel);
    Task<ExtensionCatalogDetails?> CatalogDetailsAsync(ExtensionCatalogIdentity identity, string extensionId,
        CancellationToken cancellationToken);
    Task<ExtensionCatalogView> RefreshAsync(string subject, ExtensionStoreChannel channel, CancellationToken cancellationToken);
    Task<ExtensionApplyAccepted> ApplyAsync(Guid operationId, string subject, ExtensionApplyCommand request,
        CancellationToken clientCancellation);
    Task StopAsync(TimeSpan timeout);
}
