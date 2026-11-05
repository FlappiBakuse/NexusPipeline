using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InstalledMaintenanceScope : IDisposable
{
    internal string Root { get; }
    internal string Target => Path.Combine(Root, "installed");
    internal InstallerPaths Paths { get; }
    internal InstallationIdentity Original { get; }
    private readonly string? _previous;
    private readonly string _scope = Guid.NewGuid().ToString("N");

    internal InstalledMaintenanceScope(PublishedUpdateInputs inputs)
    {
        Root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "m-owned-" + _scope);
        ExclusiveDirectory.Create(Root);
        File.WriteAllText(Path.Combine(Root, ".nexus-installer-fixture"), _scope);
        _previous = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT");
        Environment.SetEnvironmentVariable("NEXUS_TEST_ROOT", Root);
        try
        {
            Paths = InstallerPaths.Current();
            inputs.InstallOriginal(Target, Root);
            Original = new InstallationAnchor(Paths.ManagerRoot, Paths.RegistryKey).Read(Target) ?? throw new IOException("fixture_installation_missing");
        }
        catch { Environment.SetEnvironmentVariable("NEXUS_TEST_ROOT", _previous); throw; }
    }

    internal void Uninstall()
    {
        var runtime = Path.Combine(Target, "runtime");
        var scanner = new FileTreeScanner(new(20000, 4294967296));
        var before = scanner.Capture(runtime, default);
        var objects = PhysicalTreeIdentity.Capture(runtime, before);
        var start = new ProcessStartInfo(Path.Combine(Target, InstallationAnchor.EngineEntry))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + Path.Combine(Root, "native-uninstall.log") })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("fixture_uninstall_start_failed");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180000)) { process.Kill(entireProcessTree: true); throw new IOException("fixture_uninstall_deadline"); }
        File.WriteAllText(Path.Combine(Root, "uninstall.stdout.log"), stdout.GetAwaiter().GetResult());
        File.WriteAllText(Path.Combine(Root, "uninstall.stderr.log"), stderr.GetAwaiter().GetResult());
        File.WriteAllText(Path.Combine(Root, "uninstall.command.json"), JsonSerializer.Serialize(new { start.FileName, process.ExitCode }));
        if (process.ExitCode != 0) throw new IOException("fixture_native_uninstall_failed");
        var after = scanner.Capture(runtime, default);
        if (!before.Entries.SequenceEqual(after.Entries) || !objects.SequenceEqual(PhysicalTreeIdentity.Capture(runtime, after)))
            throw new IOException("fixture_uninstall_changed_runtime");
        var retained = new InstallationAnchor(Paths.ManagerRoot, Paths.RegistryKey).Read(Target);
        if (retained is not { State: "retained-data" } || retained.InstallationId != Original.InstallationId || retained.RuntimeId != Original.RuntimeId)
            throw new IOException("fixture_native_retention_failed");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NEXUS_TEST_ROOT", _previous);
        Registry.CurrentUser.DeleteSubKeyTree(Paths.RegistryKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(Paths.UninstallRegistryKey, throwOnMissingSubKey: false);
    }
}
