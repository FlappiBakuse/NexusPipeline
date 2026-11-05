using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InstallationCrashFixture : IAsyncDisposable
{
    private readonly OwnedCrashProcess _owner = new();
    internal readonly BoundPackageFixture Package;
    internal readonly PayloadApplyRoots Roots;
    internal readonly string InputPath, AcceptedReport, BarrierReport, ActivationDirectory;
    internal readonly string Point;
    internal readonly Guid OperationId;
    internal OwnedWorkerIdentity Native { get; private set; } = null!;
    internal JsonElement Accepted { get; private set; }
    private bool _started, _stopped;
    internal RuntimeInstance Instance => Package.Catalog.Instance;
    internal Guid JournalId => Accepted.GetProperty("JournalId").GetGuid();
    internal BlobReference Plan => Accepted.GetProperty("Plan").Deserialize<BlobReference>()!;

    internal InstallationCrashFixture(string point, PayloadAction? followup = null, string? physicalStage = null, bool hostMedia = false)
    {
        Point = point;
        ActivationDirectory = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!,
            "installation-callback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ActivationDirectory);
        try
        {
            var managed = point is ("callback" or "failed-restoration") || followup.HasValue || hostMedia;
            Package = new(managed ? "managed-code" : "data-specialized",
                manifest =>
                {
                    if (managed) manifest["managed"]!["entryType"] = "Controlled.ManagedExtension";
                    if (hostMedia) manifest["capabilities"] = new System.Text.Json.Nodes.JsonArray("media.capture");
                }, directory =>
                {
                    if (!managed) return;
                    File.Copy(Environment.GetEnvironmentVariable("NEXUS_MANAGED_EXTENSION_FIXTURE_DLL")
                        ?? throw new IOException("NEXUS_MANAGED_EXTENSION_FIXTURE_DLL_required"),
                        Path.Combine(directory, "entry.dll"), overwrite: true);
                    File.WriteAllBytes(Path.Combine(directory, "activation-input.json"),
                        JsonSerializer.SerializeToUtf8Bytes(new { Directory = ActivationDirectory,
                            Nonce = Guid.NewGuid().ToString("N"), Mode = hostMedia ? "media-frame" : point == "callback" ? "late" : "return" }));
                }, catalog: hostMedia ? new ExtensionCatalogFixture(useHostTestTrust: true) : null);
            Package.Publish();
            if (hostMedia)
            {
                var settings = new HostSettingsStore(Package.Catalog.Database); var before = settings.Read();
                var change = new NexusPipeline.Core.Settings.HostSettingsChange(before.Revision,
                    before.Values with { Extensions = [new("fixture-plugin", false)] });
                settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
            }
            BoundPackageFixture? next = null;
            SignedCatalogBytes? signed = null;
            if (followup == PayloadAction.Upgrade)
            {
                var fails = point == "failed-restoration";
                var failureDirectory = Path.Combine(ActivationDirectory, "failed"); Directory.CreateDirectory(failureDirectory);
                next = new(fails ? "managed-code" : "data-specialized", manifest =>
                {
                    manifest["packageVersion"] = "0.6.0";
                    if (fails) manifest["managed"]!["entryType"] = "Controlled.ManagedExtension";
                }, directory =>
                {
                    if (!fails) return;
                    File.Copy(Environment.GetEnvironmentVariable("NEXUS_MANAGED_EXTENSION_FIXTURE_DLL")
                        ?? throw new IOException("NEXUS_MANAGED_EXTENSION_FIXTURE_DLL_required"),
                        Path.Combine(directory, "entry.dll"), overwrite: true);
                    File.WriteAllBytes(Path.Combine(directory, "activation-input.json"),
                        JsonSerializer.SerializeToUtf8Bytes(new { Directory = failureDirectory,
                            Nonce = Guid.NewGuid().ToString("N"), Mode = "throw" }));
                }, catalog: Package.Catalog);
                signed = next.Signed(2, entry => entry["packageVersion"] = "0.6.0");
            }
            OperationId = followup.HasValue ? next?.OperationId ?? Guid.NewGuid() : Package.OperationId;
            var parent = Path.Combine(Instance.RuntimeRoot, "extensions"); Directory.CreateDirectory(parent);
            Roots = new(Path.Combine(Package.Root, "host"), parent);
            if (hostMedia) MaintenanceBinaryFixture.CopyBuild(Roots.HostPayloadRoot);
            InputPath = Path.Combine(Package.Root, "crash-input.json");
            AcceptedReport = Path.Combine(Package.Root, "accepted.json"); BarrierReport = Path.Combine(Package.Root, "barrier.json");
            File.WriteAllBytes(InputPath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Instance, Package.Archive, PayloadParent = parent, HostRoot = Roots.HostPayloadRoot, OperationId,
                PublicKey = Package.Catalog.Signer.ExportSubjectPublicKeyInfo(), Point = point, AcceptedReport, BarrierReport,
                NextArchive = next?.Archive, NextPointer = signed?.Pointer, NextCatalog = signed?.Catalog, NextSignature = signed?.Signature,
                FollowupAction = followup, InitialOperationId = followup.HasValue ? Package.OperationId : (Guid?)null,
                PhysicalStage = physicalStage
            }));
            Package.Catalog.Lease.Dispose();
        }
        catch { _owner.Dispose(); Package?.Dispose(); throw; }
    }

    internal async Task StartAsync()
    {
        Native = _owner.Start(["extension-installation-recovery", InputPath], Package.Root, Instance.InstanceId,
            OperationId, Path.Combine(Package.Root, "native.json"));
        _started = true;
        Accepted = await _owner.ReadReportAsync(AcceptedReport, TimeSpan.FromSeconds(60));
        if (Accepted.GetProperty("processId").GetInt32() != Native.ProcessId ||
            Accepted.GetProperty("instance").Deserialize<RuntimeInstance>() != Instance) throw new IOException("fixture_owner_unbound");
        if (Point == "callback")
            _ = await OwnedCrashProcess.ReadReportAsync(Path.Combine(ActivationDirectory, "activated.json"), TimeSpan.FromSeconds(15));
        else
        {
            var barrier = await _owner.ReadReportAsync(BarrierReport, TimeSpan.FromSeconds(60));
            if (barrier.GetProperty("processId").GetInt32() != Native.ProcessId ||
                barrier.GetProperty("journalId").GetGuid() != JournalId) throw new IOException("fixture_barrier_unbound");
            if (Point == "complete-exit" && await _owner.AwaitExitAsync() != 0) throw new IOException("fixture_install_exit_failed");
        }
    }

    internal async Task<OwnedCrashStop> StopAsync()
    {
        var actual = await _owner.StopAsync(); _stopped = true; _ = await _owner.AwaitExitAsync(); return actual;
    }

    internal byte[][] History() => new JournalStore(Instance, Package.Catalog.Contracts)
        .ReadAllRevisions(JournalId).Select(item => item.Export()).ToArray();

    public async ValueTask DisposeAsync()
    {
        try { if (_started && !_stopped) _ = await StopAsync(); }
        finally { _owner.Dispose(); Package.Dispose(); }
    }
}
