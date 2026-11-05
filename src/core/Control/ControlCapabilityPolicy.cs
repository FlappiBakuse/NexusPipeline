namespace NexusPipeline.Core.Control;

public enum ControlOrigin { Local, Remote, Mcp }
public enum ControlCapability
{
    CapabilitiesRead, StatusRead, DoctorRead, OperationsRead,
    AutomationRead, AutomationWrite, AutomationDelete, AutomationExchange,
    ConfigurationRead, ConfigurationEdit, ConfigurationRepair, NativeConfigurationOpen,
    ExecutionPreview, ExecutionStart, ExecutionCancel, SchedulingRead,
    RecordsRead, LogsRead, ImagesRead, RecordsRetire,
    SettingsRead, SettingsWrite, SecretStatus, SecretRead, SecretWrite,
    NotificationsRead, NotificationsWrite, NotificationsDelete, NotificationsTest,
    ExtensionsRead, ExtensionsWrite, Desktop, CompletionLocal, MaintenanceRead, MaintenanceWrite, MediaGrant,
    AutomationDeclare
}
public sealed record ControlCapabilityDescription(ControlCapability Id, string Name, bool IsWrite, bool IsLocalOnly, bool IsMcpTool);
public sealed record ControlPermissionSnapshot(bool McpEnabled, bool McpWriteEnabled);

public static class ControlCapabilityPolicy
{
    private static readonly ControlCapabilityDescription[] Definitions =
    [
        new(ControlCapability.CapabilitiesRead, "control.capabilities.read", false, false, true),
        new(ControlCapability.StatusRead, "control.status.read", false, false, true),
        new(ControlCapability.DoctorRead, "control.doctor.read", false, false, true),
        new(ControlCapability.OperationsRead, "operations.read", false, false, true),
        new(ControlCapability.AutomationRead, "automation.read", false, false, true),
        new(ControlCapability.AutomationWrite, "automation.write", true, false, false),
        new(ControlCapability.AutomationDeclare, "automation.declare", true, true, false),
        new(ControlCapability.AutomationDelete, "automation.delete", true, false, false),
        new(ControlCapability.AutomationExchange, "automation.exchange", true, false, false),
        new(ControlCapability.ConfigurationRead, "configuration.read", false, false, true),
        new(ControlCapability.ConfigurationEdit, "configuration.edit", true, false, false),
        new(ControlCapability.ConfigurationRepair, "configuration.repair", true, true, false),
        new(ControlCapability.NativeConfigurationOpen, "configuration.native.open", true, true, false),
        new(ControlCapability.ExecutionPreview, "execution.preview", false, false, true),
        new(ControlCapability.ExecutionStart, "execution.start", true, false, true),
        new(ControlCapability.ExecutionCancel, "execution.cancel", true, false, true),
        new(ControlCapability.SchedulingRead, "scheduling.read", false, false, true),
        new(ControlCapability.RecordsRead, "records.read", false, false, true),
        new(ControlCapability.LogsRead, "records.logs.read", false, false, true),
        new(ControlCapability.ImagesRead, "records.images.read", false, false, true),
        new(ControlCapability.RecordsRetire, "records.retire", true, true, false),
        new(ControlCapability.SettingsRead, "settings.read", false, false, false),
        new(ControlCapability.SettingsWrite, "settings.write", true, false, false),
        new(ControlCapability.SecretStatus, "secrets.status", false, true, false),
        new(ControlCapability.SecretRead, "secrets.read", false, true, false),
        new(ControlCapability.SecretWrite, "secrets.write", true, true, false),
        new(ControlCapability.NotificationsRead, "notifications.read", false, false, true),
        new(ControlCapability.NotificationsWrite, "notifications.write", true, false, true),
        new(ControlCapability.NotificationsDelete, "notifications.delete", true, false, true),
        new(ControlCapability.NotificationsTest, "notifications.test", true, false, true),
        new(ControlCapability.ExtensionsRead, "extensions.read", false, false, false),
        new(ControlCapability.ExtensionsWrite, "extensions.write", true, false, false),
        new(ControlCapability.Desktop, "desktop.local", true, true, false),
        new(ControlCapability.CompletionLocal, "completion.local", true, true, false),
        new(ControlCapability.MaintenanceRead, "maintenance.read", false, false, false),
        new(ControlCapability.MaintenanceWrite, "maintenance.write", true, true, false),
        new(ControlCapability.MediaGrant, "media.preview.grant", true, false, false)
    ];
    public static ControlCapabilityDescription Describe(ControlCapability capability) =>
        Definitions.SingleOrDefault(value => value.Id == capability) ?? throw new ArgumentException("control_capability_unknown");

    public static bool IsAllowed(ControlOrigin origin, ControlCapability capability, ControlPermissionSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var description = Describe(capability);
        if (!Enum.IsDefined(origin)) return false;
        if (origin == ControlOrigin.Local) return true;
        if (description.IsLocalOnly) return false;
        if (origin == ControlOrigin.Remote) return true;
        return current.McpEnabled && description.IsMcpTool && (!description.IsWrite || current.McpWriteEnabled);
    }
    public static IReadOnlyList<ControlCapabilityDescription> Visible(ControlOrigin origin, ControlPermissionSnapshot current) =>
        Array.AsReadOnly(Definitions.Where(value => IsAllowed(origin, value.Id, current)).ToArray());
}
