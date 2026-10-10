using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Modules.Dashboard;

internal sealed record DashboardCard(string CardId, string SourceKind, string? PluginName, string LocalId,
    string Title, string Description, bool DefaultVisible, int DefaultOrder);
internal sealed record DashboardSourceSnapshot(string Revision, IReadOnlyList<DashboardCard> Cards);
internal interface IDashboardCardSource
{
    DashboardSourceSnapshot Read(string locale);
    event Action? Changed;
}
internal sealed record DashboardEntry(string CardId, bool Visible);
internal sealed record DashboardLayout(int SchemaVersion, string LayoutId, long Revision, DateTime UpdatedAtUtc, DashboardEntry[] Entries);
internal sealed record DashboardSnapshot(int SchemaVersion, string CatalogRevision, IReadOnlyList<DashboardCard> Cards,
    DashboardLayout Layout, string[] VisibleCardIds);
internal sealed record DashboardCatalog(int SchemaVersion, string CatalogRevision, IReadOnlyList<DashboardCard> Cards);
internal sealed record DashboardWriteRequest(int SchemaVersion, string? CatalogRevision, string[]? VisibleCardIds);
internal sealed record DashboardChange(string Kind, string? LayoutId, long? Revision, string? CatalogRevision);

internal enum DashboardFailureKind
{
    PreconditionRequired, PreconditionInvalid, CatalogChanged, Conflict, CardInvalid, Invalid, TooLarge,
    PersistenceFailed, Unavailable
}
internal sealed class DashboardFailure(DashboardFailureKind kind, Exception? inner = null) : Exception(kind.ToString(), inner)
{
    public DashboardFailureKind Kind { get; } = kind;
}

internal sealed record DashboardRepresentation(DashboardSnapshot Snapshot, byte[] Body, string ETag)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false
    };

    internal static DashboardRepresentation Create(DashboardSnapshot snapshot)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        return new(snapshot, body, "\"dashboard-sha256-" + Convert.ToHexStringLower(SHA256.HashData(body)) + "\"");
    }
}
