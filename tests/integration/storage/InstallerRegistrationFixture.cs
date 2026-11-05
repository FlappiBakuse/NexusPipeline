using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InstallerRegistrationFixture : IDisposable
{
    internal string Root { get; }
    internal string Application => Path.Combine(Root, "application");
    internal string Candidate => Path.Combine(Root, "candidate");
    internal InstallerPaths Paths { get; }
    internal Guid Transaction { get; } = Guid.NewGuid();
    internal string Digest { get; }
    internal InstallerTargetManifest Target { get; }
    internal string TargetPath => Path.Combine(Root, "target-manifest.json");
    internal string WorkerPath { get; }
    internal InstallerRegistration Owner { get; }
    internal string Checkpoint => Path.Combine(Paths.ManagerRoot, "metadata-checkpoint");
    private int _command;

    internal InstallerRegistrationFixture()
    {
        var scope = Guid.NewGuid().ToString("N");
        Root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "installer-registration-" + scope);
        Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, ".nexus-installer-fixture"), scope);
        Paths = new(Path.Combine(Root, "installer-manager"), @"Software\NexusPipeline\Tests\Installer\" + scope);
        ApplicationPayloadSeed.Prepare(Candidate);
        var receipt = Environment.GetEnvironmentVariable("NEXUS_PRODUCT_ASSEMBLY") ?? throw new IOException("product_assembly_required");
        using var document = JsonDocument.Parse(File.ReadAllBytes(receipt));
        if (document.RootElement.GetProperty("status").GetString() != "PASS_APPLICATION_ASSEMBLED" ||
            document.RootElement.GetProperty("profile").GetString() != "test") throw new IOException("product_assembly_invalid");
        var pointer = document.RootElement.GetProperty("publishReceipt");
        var publication = File.ReadAllBytes(pointer.GetProperty("receipt").GetString()!);
        if (ApplicationPayloadSeed.Digest(publication) != pointer.GetProperty("sha256").GetString()) throw new IOException("publication_changed");
        using var published = JsonDocument.Parse(publication);
        var installer = published.RootElement.GetProperty("installerWorker");
        WorkerPath = installer.GetProperty("path").GetString()!;
        if (ApplicationPayloadSeed.Digest(File.ReadAllBytes(WorkerPath)) != installer.GetProperty("file").GetProperty("sha256").GetString())
            throw new IOException("installer_worker_changed");
        Owner = new(Paths, "test", WorkerPath);
        foreach (var name in new[] { ApplicationTree.HostEntry, ApplicationTree.MaintenanceEntry })
            File.Copy(Path.Combine(Path.GetDirectoryName(receipt)!, name), Path.Combine(Candidate, name), overwrite: true);
        var target = ApplicationPayloadSeed.Write(Candidate);
        Digest = ApplicationPayloadSeed.Digest(target);
        Target = InstallerTargetManifest.Read(target, Digest, "test");
        File.WriteAllBytes(TargetPath, target);
    }

    internal void Begin() => Owner.Begin(Application, Transaction, Target, Parent());

    internal void InstallPayload()
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(Candidate))
        {
            var destination = Path.Combine(Application, Path.GetFileName(entry));
            if (Directory.Exists(entry)) Directory.Move(entry, destination); else File.Move(entry, destination);
        }
        var engine = Path.Combine(Application, InstallationAnchor.EngineEntry);
        Directory.CreateDirectory(Path.GetDirectoryName(engine)!);
        File.WriteAllBytes(engine, "Controlled native Setup engine"u8.ToArray());
        File.WriteAllBytes(Path.ChangeExtension(engine, "dat"), "Controlled native Setup state"u8.ToArray());
        using var key = Registry.CurrentUser.CreateSubKey(Paths.UninstallRegistryKey);
        key.SetValue("InstallLocation", Application, RegistryValueKind.String);
        key.SetValue("DisplayVersion", "0.18.0", RegistryValueKind.String);
        key.SetValue("UninstallString", '"' + engine.Replace('/', '\\') + '"', RegistryValueKind.String);
    }

    internal async Task<(int ExitCode, string Output)> Execute(params string[] command)
    {
        var executable = WorkerPath;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in command) start.ArgumentList.Add(argument);
        start.Environment["NEXUS_TEST_ROOT"] = Root;
        using var process = Process.Start(start) ?? throw new IOException("installer_fixture_start_failed");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } throw; }
        var stdout = await output; var stderr = await error; var label = "entry-" + ++_command;
        File.WriteAllText(Path.Combine(Root, label + ".stdout.log"), stdout);
        File.WriteAllText(Path.Combine(Root, label + ".stderr.log"), stderr);
        File.WriteAllText(Path.Combine(Root, label + ".command.json"), JsonSerializer.Serialize(new { executable, command, process.ExitCode }));
        return (process.ExitCode, stdout);
    }

    private static ProcessIdentity Parent()
    {
        var observed = ProcessSnapshotProbe.Capture(Environment.ProcessId, "", default);
        if (observed.Truncated || observed.Processes.Count != 1) throw new IOException("installer_parent_unverified");
        return ProcessIdentityReader.Read(observed.Processes[0].Ppid);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Paths.RegistryKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(Paths.UninstallRegistryKey, throwOnMissingSubKey: false);
    }
}
