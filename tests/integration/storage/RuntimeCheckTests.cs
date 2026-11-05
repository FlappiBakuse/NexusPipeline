using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Records;
using NexusPipeline.Host.Control.Runtime;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class RuntimeCheckTests
{
    [Fact(DisplayName = "Diagnostics Respect Selected Tool Applicability And Report Missing Files And Unhealthy Owners Without Exporting Private Reasons")]
    public void ReportsApplicableTools()
    {
        const string canary = "private-diagnostic-canary";
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "runtime-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Guid.NewGuid();
        var tool = new AutomationPath(source, "selected-adb.exe");
        var script = new AutomationScript(Guid.NewGuid(), 1, canary, AutomationKind.Generic, source, null,
            new(source, "unused.exe"), [], [], [], new(1, 0, 0), new(AutomationJudgementLanguage.Python, [], [], null))
        { Target = new(AutomationTargetKind.Android, false, 1, false, null,
            new(AutomationAndroidDriver.GenericAdb, tool, canary, "fixture.package", [])) };
        var catalog = AutomationCatalog.Empty with { Roots = [new(source, 1, canary, AutomationRootKind.External, root)], Scripts = [script] };
        var extension = new DiagnosticExtension("fixture-extension", "0.5.0", "managed-code");
        var details = new RuntimeDiagnosticDetails(Environment.Version.ToString(), "X64", "ready", 0, 0, 1, 1, 0, [])
        { Extensions = [extension], OpenConfigurationCount = 1, PendingUpdateCount = 1, PendingExtensionCount = 1 };
        var report = new RuntimeDoctorReport("nexus.runtime.diagnostic", 1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            DiagnosticAdmissionState.Ready, 1, 0, 0, 0, 0, 0, details);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", root);
            var checks = new RuntimeChecks(root, 1234, null, () => catalog, new ExtensionOwner(canary));
            var missing = checks.Read(report, false, false, false);
            Assert.Equal("fail", Check(missing, "dependency.python"));
            Assert.Equal("fail", Check(missing, "dependency.adb"));
            Assert.Equal("warn", Check(missing, "plugin.runtime"));
            Assert.Equal("warn", Check(missing, "config.recovery"));
            Assert.Equal("warn", Check(missing, "update.transaction"));
            Assert.Equal("pass", Check(missing, "host.install-write"));
            File.WriteAllBytes(Path.Combine(root, "python.exe"), []);
            File.WriteAllBytes(Path.Combine(root, "selected-adb.exe"), []);
            var available = checks.Read(report, true, true, true);
            Assert.Equal("pass", Check(available, "dependency.python"));
            Assert.Equal("pass", Check(available, "dependency.adb"));
            var serialized = JsonSerializer.Serialize(available);
            Assert.DoesNotContain(canary, serialized); Assert.DoesNotContain(root, serialized);
            Assert.True(Assert.Single(available.Details!.Extensions).HasFailure);
            var absent = new RuntimeChecks(Path.Combine(root, "absent"), 1234, 1235, () => AutomationCatalog.Empty,
                new ExtensionOwner(canary)).Read(report, true, true, true);
            Assert.Equal("fail", Check(absent, "host.install-write"));
            Assert.Equal("notRequired", Check(absent, "dependency.python"));
            Assert.Equal("notRequired", Check(absent, "dependency.adb"));
            Assert.Equal("pass", Check(absent, "mcp.listener"));
            Assert.Empty(Directory.GetFiles(root, ".nexus-doctor-*"));
        }
        finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
    }

    private static string Check(RuntimeDoctorReport report, string id) => report.Details!.Checks.Single(value => value.Id == id).Status;

    private sealed class ExtensionOwner(string reason) : IExtensionRuntimeLifecycle
    {
        public IReadOnlyList<ExtensionRuntimeState> ReadStates() => [new("fixture-extension", 1, true, true, null, reason)];
        public Task StartAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ExtensionRuntimeState> ReconcileAsync(ExtensionRuntimeSelection selection, CancellationToken acceptedHostCancellation)
            => throw new NotSupportedException();
        public Task StopAsync(TimeSpan timeout) => throw new NotSupportedException();
    }
}
