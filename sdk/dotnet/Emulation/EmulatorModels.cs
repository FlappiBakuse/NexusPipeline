namespace NexusPipeline.Extensibility.Emulation;

/// <summary>Vendor semantics are provided by the selected extension, independently of generic Android control.</summary>
public enum EmulatorVendor
{
    /// <summary>LDPlayer installations controlled by the selected ldconsole or dnconsole tool.</summary>
    LdPlayer,
    /// <summary>Nox installations with separately identified virtual machines.</summary>
    Nox,
    /// <summary>BlueStacks installations with explicit instance identifiers.</summary>
    BlueStacks
}

/// <summary>A Host-captured tool identity. A path in this value never grants execution authority.</summary>
public sealed record EmulatorTool(string Id, string Path, string PhysicalId, string Sha256, long Revision);
/// <summary>A copy of the original bounded configuration bytes and their Host-captured identity.</summary>
public sealed record EmulatorConfig(string Id, string RelativePath, string PhysicalId, string Sha256, long Revision, ReadOnlyMemory<byte> Bytes);
/// <summary>One physical installation and its complete captured vendor configuration set.</summary>
public sealed record EmulatorInstallation(string Id, EmulatorVendor Vendor, string RootPath, string RootPhysicalId,
    EmulatorTool Control, EmulatorTool? Adb, IReadOnlyList<EmulatorConfig> Configurations);
/// <summary>Display names are separate from the vendor's persistent instance selector.</summary>
public sealed record EmulatorInstance(string Identity, string DisplayName, string? ControlName, int? Index, int? ProcessId,
    IReadOnlyList<int> CandidatePorts, IReadOnlyList<string> ConfigIds)
{
    /// <summary>The provider's stable selection ID includes the physical installation and vendor instance selector.</summary>
    public string? TargetId { get; init; }
}
/// <summary>An empty list is a successful observation rather than proof of a ready device.</summary>
public sealed record EmulatorEnumeration(string State, IReadOnlyList<EmulatorInstance> Instances);
/// <summary>A selected instance and its frozen source fingerprint, without filesystem or process authority.</summary>
public sealed record EmulatorTarget(string Id, EmulatorInstallation Installation, EmulatorInstance Instance,
    string Endpoint, string Fingerprint, string? InstanceEvidenceId = null);
/// <summary>A Host-owned process observation. Cached PID or image names do not grant stop permission.</summary>
public sealed record EmulatorProcess(string EvidenceId, string InstallationId, int ProcessId, string ImagePhysicalId,
    string ImageSha256, DateTimeOffset StartedAt, IReadOnlyList<string> Arguments);
/// <summary>A fixed vendor operation against a Host-bound tool; the Host independently admits each operation.</summary>
public sealed record EmulatorCommand(string ToolId, IReadOnlyList<string> Arguments, bool Detached = false);
/// <summary>Bounded inventory hints. The Host owns permitted roots and never treats hints as a file grant.</summary>
public sealed record EmulatorLayout(EmulatorVendor Vendor, string DisplayName, IReadOnlyList<string> InstallHints,
    IReadOnlyList<string> ControlPaths, IReadOnlyList<string> AdbPaths, IReadOnlyList<string> ConfigPaths,
    IReadOnlyList<string> SharedDataPaths, IReadOnlyList<string> WorkerNames);
/// <summary>One installation's parsed native enumeration.</summary>
public sealed record EmulatorSource(EmulatorInstallation Installation, EmulatorEnumeration Enumeration);
/// <summary>A vendor-specific refusal without exposing configuration bytes or command output.</summary>
public sealed record EmulatorDiagnostic(EmulatorVendor Vendor, string? InstallationId, string Code);
/// <summary>Independent vendor failures remain visible alongside successfully discovered installations.</summary>
public sealed record EmulatorDiscoveryResult(string State, IReadOnlyList<EmulatorSource> Sources, IReadOnlyList<EmulatorDiagnostic> Diagnostics);
