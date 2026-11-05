using NexusPipeline.Core.Control;
using NexusPipeline.Core.Settings;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class ControlCapabilityTests
{
    [Theory(DisplayName = "Every Local Capability Ignores Both Mcp Permission Flags")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IgnoresLocalFlags(bool enabled, bool writes)
    {
        var current = new ControlPermissionSnapshot(enabled, writes);
        var capabilities = Enum.GetValues<ControlCapability>();
        Assert.Equal(capabilities.Length, ControlCapabilityPolicy.Visible(ControlOrigin.Local, current).Count);
        foreach (var capability in capabilities)
            Assert.True(ControlCapabilityPolicy.IsAllowed(ControlOrigin.Local, capability, current));
    }

    [Theory(DisplayName = "Mcp Tools And Calls Use The Independent Current Enable And Write Permissions")]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, true)]
    public void SeparatesMcpPermissions(bool enabled, bool writes, bool canRead, bool canWrite)
    {
        var current = new ControlPermissionSnapshot(enabled, writes);
        var visible = ControlCapabilityPolicy.Visible(ControlOrigin.Mcp, current);
        Assert.Equal(canRead, ControlCapabilityPolicy.IsAllowed(ControlOrigin.Mcp, ControlCapability.NotificationsRead, current));
        Assert.Equal(canWrite, ControlCapabilityPolicy.IsAllowed(ControlOrigin.Mcp, ControlCapability.NotificationsWrite, current));
        Assert.Equal(canWrite, visible.Any(value => value.Id == ControlCapability.NotificationsWrite));
        Assert.Equal(canWrite, ControlCapabilityPolicy.IsAllowed(ControlOrigin.Mcp, ControlCapability.NotificationsDelete, current));
        Assert.Equal(canWrite, ControlCapabilityPolicy.IsAllowed(ControlOrigin.Mcp, ControlCapability.NotificationsTest, current));
    }

    [Fact(DisplayName = "Neither Remote Nor Writable Mcp Can Borrow Native Secret Installer Or Power Capabilities")]
    public void RestrictsNativeCapabilities()
    {
        Assert.False(HostSettings.Default.Service.McpEnabled);
        Assert.False(HostSettings.Default.Service.McpWriteEnabled);
        var current = new ControlPermissionSnapshot(true, true);
        foreach (var capability in new[] { ControlCapability.SecretRead, ControlCapability.SecretWrite,
            ControlCapability.MaintenanceWrite, ControlCapability.CompletionLocal,
            ControlCapability.Desktop, ControlCapability.RecordsRetire })
        {
            Assert.False(ControlCapabilityPolicy.IsAllowed(ControlOrigin.Mcp, capability, current));
            Assert.False(ControlCapabilityPolicy.IsAllowed(ControlOrigin.Remote, capability, current));
            Assert.True(ControlCapabilityPolicy.IsAllowed(ControlOrigin.Local, capability, current));
        }
        Assert.False(ControlCapabilityPolicy.IsAllowed(ControlOrigin.Mcp, ControlCapability.AutomationDelete, current));
        Assert.True(ControlCapabilityPolicy.IsAllowed(ControlOrigin.Remote, ControlCapability.ExecutionStart, current));
        Assert.True(ControlCapabilityPolicy.IsAllowed(ControlOrigin.Mcp, ControlCapability.ExecutionCancel, current));
        foreach (var capability in new[] { ControlCapability.SettingsWrite, ControlCapability.ExtensionsWrite, ControlCapability.MediaGrant })
        {
            Assert.True(ControlCapabilityPolicy.IsAllowed(ControlOrigin.Remote, capability, current));
            Assert.False(ControlCapabilityPolicy.IsAllowed(ControlOrigin.Mcp, capability, current));
        }
    }
}
