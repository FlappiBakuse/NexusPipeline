using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Host.Control.Runtime;

[SupportedOSPlatform("windows")]
internal sealed class RuntimeChecks(string payloadRoot, int webPort, int? mcpPort,
    Func<AutomationCatalog> definitions, IExtensionRuntimeLifecycle extensions)
{
    internal RuntimeDoctorReport Read(RuntimeDoctorReport report, bool executionReady, bool schedulingReady, bool updatesReady)
    {
        var details = report.Details ?? throw new IOException("diagnostic_details_required");
        var checks = new List<DiagnosticCheck>
        {
            new("host.version", "pass", "host.current"),
            Administrator(), InstallWrite(),
            new("web.listener", "pass", "listener.bound", webPort),
            new("mcp.listener", mcpPort is null ? "notRequired" : "pass", mcpPort is null ? "listener.disabled" : "listener.bound", mcpPort),
            new("recovery.state", report.Admission == DiagnosticAdmissionState.Ready ? "pass" : "fail", "recovery.inspect", report.IsolatedResourceCount + report.HeldResourceCount),
            new("update.transaction", details.PendingUpdateCount == 0 && updatesReady ? "pass" : "warn", "update.inspect", details.PendingUpdateCount),
            new("config.recovery", details.OpenConfigurationCount == 0 ? "pass" : "warn", "config.inspect", details.OpenConfigurationCount),
            new("execution.state", executionReady ? "pass" : "warn", "execution.inspect", report.PendingOperationCount),
            new("scheduler.state", schedulingReady ? "pass" : "warn", "scheduler.inspect", details.WaitingOccurrenceCount),
            new("plugin.pending", details.PendingExtensionCount == 0 ? "pass" : "warn", "plugin.recover", details.PendingExtensionCount),
            new("logs.recent", details.SafeLogTail.Count == 0 ? "notRequired" : "pass", "logs.typed", details.SafeLogTail.Count)
        };
        DiagnosticExtension[] installed;
        var pluginKnown = true;
        try
        {
            var states = extensions.ReadStates().ToDictionary(value => value.ExtensionId, StringComparer.Ordinal);
            installed = details.Extensions.Select(value => states.TryGetValue(value.Id, out var state)
                ? value with { Enabled = state.DesiredEnabled, Active = state.ActivationId is not null, HasFailure = state.UnavailableReason is not null }
                : value).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { installed = details.Extensions.ToArray(); pluginKnown = false; }
        var unhealthy = installed.Count(value => value.Enabled == true && (value.Active != true || value.HasFailure == true));
        checks.Add(new("plugin.runtime", !pluginKnown ? "notRun" : installed.Length == 0 ? "notRequired" : unhealthy == 0 ? "pass" : "warn", "plugin.inspect", unhealthy));
        AddDependencies(checks);
        return report with { Details = details with { Checks = checks.AsReadOnly(), Extensions = Array.AsReadOnly(installed) } };
    }

    private void AddDependencies(List<DiagnosticCheck> checks)
    {
        AutomationCatalog catalog;
        try { catalog = definitions(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or AutomationDefinitionException)
        {
            checks.Add(new("dependency.python", "notRun", "python.available"));
            checks.Add(new("dependency.adb", "notRun", "adb.selected"));
            return;
        }
        var python = catalog.Scripts.Count(value => value.Judgement.Language == AutomationJudgementLanguage.Python);
        checks.Add(new("dependency.python", python == 0 ? "notRequired" : JudgementRuntimeDiscovery.FindPython() is not null ? "pass" : "fail",
            "python.available", python));
        var targets = catalog.Scripts.Where(value => value.Target?.Kind == AutomationTargetKind.Android)
            .Select(value => value.Target!.Android!).ToArray();
        var missing = targets.Count(value => !ToolExists(catalog, value.Tool) || value.ScreenshotTool is { } screenshot && !ToolExists(catalog, screenshot));
        checks.Add(new("dependency.adb", targets.Length == 0 ? "notRequired" : missing == 0 ? "pass" : "fail", "adb.selected", missing));
    }

    internal static object EnvironmentSummary() => new
    {
        os = RuntimeInformation.OSDescription, framework = RuntimeInformation.FrameworkDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        osArchitecture = RuntimeInformation.OSArchitecture.ToString(), processorCount = Environment.ProcessorCount,
        hostVersion = "0.18.0", sdkVersion = "0.1.0"
    };

    private static DiagnosticCheck Administrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new("host.admin-integrity", new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ? "pass" : "warn", "host.admin");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        { return new("host.admin-integrity", "notRun", "host.admin"); }
    }

    private DiagnosticCheck InstallWrite()
    {
        string? probe = null;
        try
        {
            var root = PhysicalPathPolicy.NormalizeWithoutLinks(payloadRoot);
            if (!Directory.Exists(root)) return new("host.install-write", "fail", "install.permission");
            probe = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(root, ".nexus-doctor-" + Guid.NewGuid().ToString("N")));
            using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
            { file.WriteByte(0); file.Flush(true); }
            File.Delete(probe); probe = null;
            return new("host.install-write", "pass", "install.permission");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return new("host.install-write", "fail", "install.permission"); }
        finally
        {
            if (probe is not null)
                try { File.Delete(probe); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool ToolExists(AutomationCatalog catalog, AutomationPath path)
    {
        var root = catalog.Roots.Single(value => value.Id == path.RootId).Path;
        try
        {
            var source = PhysicalPathPolicy.NormalizeWithoutLinks(root);
            var tool = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(source, path.RelativePath));
            return tool.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(tool);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
