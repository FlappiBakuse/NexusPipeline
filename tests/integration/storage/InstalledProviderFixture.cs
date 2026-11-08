using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InstalledProviderFixture : IAsyncDisposable
{
    internal readonly ExecutionAdmissionFixture Execution = new(otherExtensionId: "fixture-plugin");
    internal readonly ExecutionProviderRegistry Providers = new();
    internal readonly BoundPackageFixture Package;
    internal readonly PayloadApplyRoots Roots;
    internal readonly ResourceOperationLease Root;
    internal readonly VerifiedExtensionPackage Verified;
    internal readonly PreparedExtensionPayload Prepared;
    internal readonly PayloadAcceptanceResult Accepted;
    internal readonly ExtensionActivator Activator;
    internal readonly ExecutionAdmissionService Service;
    internal readonly ExecutionRuntimeConsumer Consumer;
    internal readonly string Marker;
    internal readonly string ActivationDirectory;
    private ExtensionActivator.ApplyReservation? _inventory;
    private PreparedExtensionActivation? _source;
    private ExtensionApplyActivation? _activation;
    private bool _committed;
    internal PlanBuilderFixture Source => Execution.Source;

    internal InstalledProviderFixture(string activationMode = "return")
    {
        Marker = Path.Combine(Source.Root, "installed-provider-ready");
        ActivationDirectory = Path.Combine(Source.Root, "managed-activation");
        Directory.CreateDirectory(ActivationDirectory);
        var nonce = Guid.NewGuid().ToString("N");
        var worker = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var runtime = JudgementInterpreterLease.Inspect(worker);
        var managed = Environment.GetEnvironmentVariable("NEXUS_MANAGED_EXTENSION_FIXTURE_DLL")
            ?? throw new IOException("NEXUS_MANAGED_EXTENSION_FIXTURE_DLL_required");
        var catalog = Execution.CreateCatalog();
        Package = new("managed-code", manifest =>
        {
            manifest["managed"]!["entryType"] = "Controlled.ManagedExtension";
            manifest["capabilities"] = new JsonArray("execution.drive");
            manifest["permissions"] = new JsonObject { ["processAccess"] = new JsonArray("owned-worker", "selected-upstream") };
            manifest["execution"] = new JsonObject
            {
                ["entryId"] = "main", ["workerEntry"] = "worker/" + Path.GetFileName(worker),
                ["arguments"] = new JsonArray("provider-fixture-worker", "terminal-success", Marker),
                ["protocol"] = "nexus.execution.provider/1", ["configurationResources"] = new JsonArray()
            };
        }, directory =>
        {
            File.Copy(managed, Path.Combine(directory, "entry.dll"), overwrite: true);
            File.WriteAllBytes(Path.Combine(directory, "activation-input.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { Directory = ActivationDirectory, Nonce = nonce, Mode = activationMode }));
            foreach (var relative in runtime.RuntimeFiles.Select(item => item.RelativePath).Append(Path.GetFileName(worker)))
            {
                var path = Path.Combine(directory, "worker", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(Path.Combine(Path.GetDirectoryName(worker)!, relative), path);
            }
        }, catalog);
        Package.Publish();
        var registry = new RecoveryJournalRegistry(Source.Database);
        _ = registry.PublishVerified(registry.ReadSnapshot().Revision);
        var parent = Path.Combine(Source.Instance.RuntimeRoot, "extensions"); Directory.CreateDirectory(parent);
        Roots = new(Path.Combine(Source.Root, "host"), parent);
        var target = Path.Combine(parent, "fixture-plugin");
        var claims = PhysicalResourceAdmission.Merge(new ResourceClaim[]
        {
            new("extension:fixture-plugin", ResourceClaimMode.Mutation), new("extensions:catalog", ResourceClaimMode.Reference),
            new("blobs:content", ResourceClaimMode.Reference), new("settings:host", ResourceClaimMode.Mutation)
        }.Concat(PhysicalResourceAdmission.Read(target))
            .Concat(PhysicalResourceAdmission.Read(ExtensionWorkspacePath.For(target, Package.OperationId))));
        Root = Execution.Resources.TryAcquire(Package.OperationId, claims).Lease ?? throw new IOException("fixture_install_root_unavailable");
        Verified = Package.Prepare(Root);
        var candidate = Verified.CaptureCandidate(Root, Package.Blobs, default);
        Prepared = PreparedExtensionPayload.PrepareFirstInstall(Source.Instance, Source.Database, Source.Lease,
            Execution.Resources, Execution.Index, Root, candidate, Roots, new(), "local", default);
        Accepted = new PayloadApplyAcceptance(Source.Instance, new(), Source.Database, Roots)
            .AcceptPrepared(Prepared, DateTimeOffset.UtcNow);
        Activator = new(new(), Source.Lease, Execution.Resources, Providers);
        _ = new InstalledPortFactory(Source.Instance, Source.Database, Source.Lease, Execution.Resources,
            Activator, new(), Execution.Index, new(null, null, null, null, null, null));
        foreach (var item in runtime.RuntimeFiles)
        {
            var path = Path.Combine(Path.GetDirectoryName(Source.OtherImage)!, item.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(worker)!, item.RelativePath), path, overwrite: true);
        }
        var definitions = Source.Definitions.Read();
        var script = definitions.Scripts.Single(item => item.Id == Source.OtherScriptId);
        var changes = new AutomationChanges(definitions.Revision, [], [script with
        {
            Limits = new(1, 120, 120), Judgement = new(AutomationJudgementLanguage.Keywords, [], [], null),
            ConfigurationPaths = [], LogPaths = [], UpdateConfigurationAtFinish = true
        }], [], [], [], [], []);
        Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "installed-provider-fixture", changes),
            changes, DateTimeOffset.UtcNow);
        Service = new(Source.Instance, Source.Database, new(), Source.Lease, Execution.Resources, Execution.Index,
            Execution.Inbox, new(15, 15, 15), payloadRoots: Roots, providers: Providers);
        Consumer = new(Source.Instance, Source.Database, new(), Source.Lease, Execution.Resources, Execution.Index, Execution.Inbox);
    }

    internal string TargetPath => _source?.TargetPath ?? throw new IOException("fixture_activation_source_missing");
    internal string ArchivePath => Path.Combine(Source.Instance.RuntimeRoot,
        new ExtensionPayloadBinding(Source.Instance, Source.Database, new(), Roots)
            .Read(Accepted.JournalId, Accepted.Plan).Source.Package!.Archive.RelativePath);

    internal async Task PublishAsync(CancellationToken cancellation = default)
    {
        _inventory = await Activator.ReserveForApplyAsync(Root, "fixture-plugin", cancellation);
        _ = new ExtensionPhysicalJournal(Source.Instance, Source.Database, new(), Roots).Apply(Prepared, _inventory, default);
        _source = PreparedExtensionActivation.Prepare(Source.Instance, Source.Database, Source.Lease, new(), Roots,
            Prepared, _inventory, cancellation);
        _activation = await _inventory.PrepareCandidateAsync(_source);
        var store = new PayloadCommitStore(Source.Instance, Source.Database, new(), Roots);
        _activation.Publish(store.PrepareActivation(Prepared, _inventory, _activation, default));
    }

    internal async Task CommitPreparationAsync()
    {
        _ = new PayloadCommitStore(Source.Instance, Source.Database, new(), Roots)
            .Commit(Prepared, _inventory!, _activation, default);
        _ = _activation!.CompleteCommitted(); _committed = true;
        await _activation.DisposeAsync(); await _source!.DisposeAsync(); _inventory!.Dispose();
        Prepared.Dispose(); Verified.Dispose(); Root.Dispose();
    }

    internal AttemptAcceptanceResult Start() => Service.Start(Guid.NewGuid(), "installed-provider-fixture",
        Source.Request(new BindingTarget(Source.OtherBinding)), DateTimeOffset.UtcNow);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_activation is not null) await _activation.DisposeAsync();
            if (_source is not null) await _source.DisposeAsync();
            _inventory?.Dispose(); Prepared.Dispose(); Verified.Dispose(); Root.Dispose();
            if (_committed)
            {
                using var root = Execution.Resources.TryAcquire(Guid.NewGuid(),
                    [new("extension:fixture-plugin", ResourceClaimMode.Mutation)]).Lease
                    ?? throw new IOException("fixture_scope_stop_pending");
                using var stopped = await Activator.ReserveForApplyAsync(root, "fixture-plugin", default);
            }
            await Providers.StopMonitorAsync();
        }
        finally { Package.Dispose(); Execution.Dispose(); }
    }
}
