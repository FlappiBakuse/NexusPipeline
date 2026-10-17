using System.Text.Json;
using System.Security.Cryptography;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Plugins.Repository;

internal sealed class PluginPendingState
{
    public int SchemaVersion { get; set; }

    public List<PluginPendingOperation> Operations { get; set; } = new();
}

internal sealed class PluginPendingOperation
{
    public string Action { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>插件的正式物理目录名。</summary>
    public string ArtifactName { get; set; } = "";

    public string Version { get; set; } = "";

    public string Kind { get; set; } = "";

    public string ApiVersion { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public string Channel { get; set; } = "stable";

    public string SourceCommit { get; set; } = "";

    public string StagedPath { get; set; } = "";

    public string BackupPath { get; set; } = "";

    public string Phase { get; set; } = "pending";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string OperationId { get; set; } = "";

    public bool EnableAfterInstall { get; set; }
}

internal sealed class PluginOwnershipState
{
    public int SchemaVersion { get; set; }

    public List<PluginOwnership> Plugins { get; set; } = new();
}

internal sealed class PluginOwnership
{
    public string Name { get; set; } = "";

    public string ArtifactName { get; set; } = "";

    public string Version { get; set; } = "";

    public string Kind { get; set; } = "";

    public string ApiVersion { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public string Channel { get; set; } = "stable";

    public string SourceCommit { get; set; } = "";

    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
}
