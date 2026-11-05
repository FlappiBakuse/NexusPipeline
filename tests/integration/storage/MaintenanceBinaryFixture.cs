using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceBinaryFixture : IDisposable
{
    internal string Root { get; }
    internal RuntimeInstance Instance { get; }
    internal StateDatabase Database { get; }
    internal FrozenPayloadApply Plan { get; private set; }
    internal string HostPath { get; }
    internal Uri Address { get; }
    internal string Subject { get; private set; }
    internal Task<string> HostErrors { get; private set; } = Task.FromResult("");
    private readonly List<Process> _processes = [];
    private readonly bool _desktop;

    internal MaintenanceBinaryFixture(bool failCandidateStartup = false, HostUpdateTiming timing = HostUpdateTiming.Now,
        bool desktop = false, bool acceptPending = true, string hostVersion = "0.18.0", PublishedUpdateInputs? published = null,
        InstalledMaintenanceScope? installed = null)
    {
        _desktop = desktop;
        Root = installed?.Root ?? Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "m-" + Guid.NewGuid().ToString("N")[..8]);
        if (installed is null) ExclusiveDirectory.Create(Root);
        var contracts = new ContractReader();
        using var user = System.Security.Principal.WindowsIdentity.GetCurrent();
        Subject = "local:" + user.User!.Value;
        var target = Path.Combine(Root, "installed");
        Directory.CreateDirectory(target);
        Instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(target, "runtime"));
        Database = StateDatabase.CreateFresh(Instance);
        if (published is null)
        {
            CopyBuild(target, desktop, bundled: true);
            if (desktop) MaintenanceDesktopBundle.CopyTo(Path.Combine(target, "resources", "desktop"));
            File.WriteAllText(Path.Combine(target, "README.md"), "original");
            ApplicationPayloadSeed.Write(target);
        }
        else if (installed is null) published.CopyOriginal(target);
        HostPath = Path.Combine(target, ApplicationTree.HostEntry);
        var candidate = Path.Combine(Root, "candidate");
        if (published is null)
        {
            CopyBuild(candidate, desktop, bundled: true);
            if (desktop) MaintenanceDesktopBundle.CopyTo(Path.Combine(candidate, "resources", "desktop"));
            File.WriteAllText(Path.Combine(candidate, "README.md"), "candidate");
        }
        else published.ExtractCandidate(candidate);
        if (failCandidateStartup && published is null)
            SingleFileFixture.FailStartup(Path.Combine(candidate, ApplicationTree.HostEntry));
        var blobs = new ImmutableBlobStore(Instance, contracts);
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), blobs);
        var plan = new FrozenPayloadApply(Instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), JournalKind.UpdateApply,
            PayloadAction.Upgrade, null, target, PhysicalObjectIdentity.Read(Root), archive.CaptureApplication(target, default),
            archive.CaptureApplication(candidate, default), ["maintenance:host-payload"]);
        Plan = (published is not null && failCandidateStartup
            ? published.BindFailedCandidate(Instance, Database, plan, candidate, timing, desktop)
            : installed is not null ? published!.BindOwnedCandidate(Instance, Database, plan, candidate, timing, desktop)
            : published?.Bind(Instance, Database, plan, candidate, timing, desktop)) ?? UpdateSourceSeed.Bind(Instance, Database, plan, candidate, executableTestPayload: true, timing: timing,
            window: desktop ? new(UpdateWindowMode.Desktop, true, "/settings") : null,
            hostVersion: hostVersion);
        var roots = new PayloadApplyRoots(target, Path.Combine(Instance.RuntimeRoot, "extensions"));
        var source = new UpdateSourceCodec(Instance, contracts).ReadMetadata(Plan.MaintenanceSource!);
        var recovery = new RecoveryIndex(); recovery.PublishVerified(0, 1, []);
        var resources = new ResourceLeaseManager(new(recovery, TimeProvider.System));
        if (acceptPending) using (var lease = InstanceLease.Acquire(Instance, contracts))
        {
            var service = new HostUpdateService(Instance, Database, lease, resources, contracts, roots,
                UpdateReleaseTrust.Configured(), () => new HttpClient(new ArtifactHandler(blobs, source)));
            try
            {
                _ = service.PrepareAsync(new(Plan.OperationId, Subject, source.Feed.Identity, source.HostVersion,
                    source.Timing, source.Window), CancellationToken.None).GetAwaiter().GetResult();
                using var connection = DatabaseConnections.Open(Database.DatabasePath, readOnly: true);
                using var transaction = connection.BeginTransaction(deferred: true);
                var row = MaintenanceOperationRows.ReadOwned(connection, transaction, Plan.OperationId, Subject)!;
                Plan = new PayloadPlanCodec(Instance, contracts, blobs, roots).Read(row.Plan);
                transaction.Commit();
            }
            finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        var number = ((IPEndPoint)port.LocalEndpoint).Port;
        Address = new($"http://127.0.0.1:{number}/");
        var store = new HostSettingsStore(Database); var before = store.Read();
        var change = new HostSettingsChange(before.Revision, before.Values with
        { Service = before.Values.Service with { WebPort = number },
            Updates = desktop ? before.Values.Updates with
                { CheckEnabled = false, AutoApplyEnabled = false, PluginAutoUpdateEnabled = false } : before.Values.Updates,
            Desktop = desktop ? before.Values.Desktop with
                { Mode = DesktopMode.Desktop, OpenOnStartup = true, MinimizeToTray = false } : before.Values.Desktop });
        store.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
    }

    internal HttpClient CreateUpdateClient()
    {
        var source = new UpdateSourceCodec(Instance, new()).ReadMetadata(Plan.MaintenanceSource!);
        return new(new ArtifactHandler(new(Instance, new()), source));
    }

    internal void UseOperation(Guid operationId, string subject)
    {
        using var connection = DatabaseConnections.Open(Database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var row = MaintenanceOperationRows.ReadOwned(connection, transaction, operationId, subject) ?? throw new IOException("fixture_pending_missing");
        Plan = new PayloadPlanCodec(Instance, new(), new(Instance, new()),
            new(Path.GetDirectoryName(HostPath)!, Path.Combine(Instance.RuntimeRoot, "extensions"))).Read(row.Plan);
        Subject = subject;
    }

    private sealed class ArtifactHandler(ImmutableBlobStore blobs, HostUpdateSource source) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = request.RequestUri!.AbsoluteUri;
            var reference = address switch
            {
                "https://updates.example.test/metadata/update/1/stable/feed.json" => source.Feed.Feed,
                "https://updates.example.test/metadata/update/1/stable/feed.sig.json" => source.Feed.Signature,
                _ when address == $"https://assets.example.test/releases/download/host/{source.HostVersion}/Host.zip" => source.Archive,
                _ => throw new IOException("fixture_unexpected_update_request")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StreamContent(blobs.OpenVerified(reference)) });
        }
    }

    internal Process Start()
    {
        var start = new ProcessStartInfo(HostPath) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var value in new[] { _desktop ? "desktop" : "serve", "--runtime-root", Instance.RuntimeRoot, "--port", "saved" }) start.ArgumentList.Add(value);
        var process = Process.Start(start) ?? throw new IOException("fixture_host_start_failed");
        HostErrors = process.StandardError.ReadToEndAsync();
        _processes.Add(process); return process;
    }

    internal Process ObserveSuccessor(int previousId)
    {
        foreach (var process in Process.GetProcessesByName("NexusPipeline"))
        {
            try
            {
                if (process.Id != previousId && !process.HasExited &&
                    StringComparer.OrdinalIgnoreCase.Equals(ProcessIdentityReader.Read(process.Id).ImagePath, HostPath))
                { _ = process.Handle; return process; }
            }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or
                System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
        throw new IOException("fixture_restored_host_missing");
    }

    internal void TrackCandidate(int processId)
    {
        if (_processes.Any(process => process.Id == processId)) return;
        _processes.Add(Process.GetProcessById(processId));
    }

    internal async Task AwaitSchedulingAsync(CancellationToken cancellation)
    {
        // HTTP readiness precedes the first background minute commit; maintenance requires its write lease to finish.
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            using (var connection = DatabaseConnections.Open(Database.DatabasePath, readOnly: true))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE operation_kind='schedule.minute' AND status='completed');";
                if ((long)command.ExecuteScalar()! == 1) return;
            }
            await Task.Delay(20, cancellation);
        }
    }

    internal async Task<Process> AwaitHelperAsync(CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return ObserveHelper(); }
            catch (IOException error) when (error.Message == "fixture_maintenance_helper_not_observed")
            { await Task.Delay(100, cancellation); }
        }
    }

    internal Process ObserveHelper()
    {
        foreach (var process in Process.GetProcessesByName("NexusPipeline.update"))
        {
            try
            {
                if (!process.HasExited && ProcessIdentityReader.Read(process.Id).ImagePath
                    .StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                { _ = process.Handle; return process; }
            }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 87 || process.HasExited) { }
            process.Dispose();
        }
        throw new IOException("fixture_maintenance_helper_not_observed");
    }

    internal static void CopyBuild(string target, bool desktop = false, bool bundled = false)
    {
        var bundle = bundled ? Environment.GetEnvironmentVariable("NEXUS_TEST_BUNDLE_ROOT")
            ?? throw new IOException("test_bundle_required") : null;
        var host = bundle is null ? Path.GetDirectoryName(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE"))!
            : Path.Combine(bundle, "host");
        var maintenance = bundle is null ? Path.GetFullPath(Path.Combine(host, "..", "..", "NexusPipeline.Maintenance", "release"))
            : Path.Combine(bundle, "maintenance");
        Directory.CreateDirectory(target);
        Copy(host); Copy(maintenance);
        void Copy(string source)
        {
            foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, path);
                var parts = relative.Split(Path.DirectorySeparatorChar);
                if (Path.GetExtension(path) == ".pdb" || parts[0] == "runtimes" && parts[1] is not ("win" or "win-x64")) continue;
                var destination = Path.Combine(target, relative switch
                {
                    "NexusPipeline.Host.exe" => ApplicationTree.HostEntry,
                    "NexusPipeline.Maintenance.exe" => ApplicationTree.MaintenanceEntry,
                    _ => relative
                });
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(destination)))
                        throw new IOException("fixture_build_files_disagree");
                }
                else File.Copy(path, destination);
            }
        }
    }

    public void Dispose()
    {
        var hashes = Plan.Before.Snapshot.Entries.Concat(Plan.Candidate.Snapshot.Entries)
            .Where(item => item.RelativePath is ApplicationTree.HostEntry or ApplicationTree.MaintenanceEntry)
            .Select(item => item.Sha256).ToHashSet(StringComparer.Ordinal);
        foreach (var name in new[] { "NexusPipeline", "NexusPipeline.update" })
        foreach (var observed in Process.GetProcessesByName(name))
        {
            try
            {
                if (observed.HasExited) continue;
                var identity = ProcessIdentityReader.Read(observed.Id);
                if (identity.ImagePath.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    hashes.Contains(identity.ImageSha256) && identity.UserSid == ProcessIdentityReader.Read(Environment.ProcessId).UserSid)
                    TrackCandidate(identity.ProcessId);
            }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 87 || observed.HasExited) { }
            finally { observed.Dispose(); }
        }
        foreach (var process in _processes.AsEnumerable().Reverse())
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(10000)) throw new IOException("fixture_process_stop_unconfirmed");
                }
            }
            finally { process.Dispose(); }
        }
        File.WriteAllText(Path.Combine(Root, "host-stderr.txt"), HostErrors.Wait(TimeSpan.FromSeconds(2))
            ? HostErrors.GetAwaiter().GetResult() : "fixture_stderr_drain_incomplete");
    }
}
