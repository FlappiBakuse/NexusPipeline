using System.Collections.Immutable;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record CatalogPackageSource(ExtensionCatalogSaved Catalog, ExtensionCatalogEntry Entry,
    BlobReference Archive);
internal sealed record ExtensionPayloadSource(string Format, int Version, Guid InstanceId, Guid OperationId, Guid JournalId,
    string ExtensionId, PayloadAction Action, long ExpectedInstalledRevision, BlobReference? OriginalFootprint,
    CatalogPackageSource? Package, long SettingsRevision, bool DesiredEnabled, ImmutableArray<ResourceClaim> Claims,
    bool InitializeEnabledPreference)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string? RemovalReason { get; init; }
}
