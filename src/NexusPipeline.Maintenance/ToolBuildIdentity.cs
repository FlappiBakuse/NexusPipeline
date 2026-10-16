using System.Text.Json;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Versioning;
namespace NexusPipeline.Maintenance;
internal sealed record ToolBuildIdentity(int SchemaVersion, string Role, string ProductVersion,
    string InstallationGeneration, string SourceSha, string SourceTreeSha, string BuildId)
{
    internal const string ResourceName = "NexusPipeline.Maintenance.BuildIdentity";
    internal static ToolBuildIdentity Read(string image, string role)
    {
        string assembly = role switch { "updater" => "NexusPipeline.update", "uninstaller" => "NexusPipeline.uninstall", "installer-metadata" => "nxp-metadata-helper", _ => throw new InvalidDataException("tool.role_unknown") };
        byte[] bytes = BundleResourceReader.Read(image, assembly + ".dll", ResourceName, 65536);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        string[] fields = ["schemaVersion", "role", "productVersion", "installationGeneration", "sourceSha", "sourceTreeSha", "buildId"];
        if (!root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal))) throw new InvalidDataException("tool.identity_fields");
        var identity = new ToolBuildIdentity(root.GetProperty("schemaVersion").GetInt32(), root.GetProperty("role").GetString()!,
            root.GetProperty("productVersion").GetString()!, root.GetProperty("installationGeneration").GetString()!,
            root.GetProperty("sourceSha").GetString()!, root.GetProperty("sourceTreeSha").GetString()!, root.GetProperty("buildId").GetString()!);
        if (identity.SchemaVersion != 1 || identity.Role != role || identity.InstallationGeneration != NexusPipeline.Platform.Storage.InstallationGeneration.Id
            || !NexusVersion.TryParse(identity.ProductVersion, out _) || !Hex(identity.SourceSha,40) || !Hex(identity.SourceTreeSha,40) || !Hex(identity.BuildId,64)) throw new InvalidDataException("tool.identity_invalid");
        return identity;
    }
    private static bool Hex(string? value, int count) => value?.Length == count && value.All(char.IsAsciiHexDigitLower);
}
