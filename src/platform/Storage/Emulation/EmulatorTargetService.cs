using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Emulation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Serialization.Protocol;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Emulation;

internal sealed record EmulatorTargetOption(EmulatorTargetSelection Selection, string Name, string Endpoint, AutomationPath Adb);
internal sealed record EmulatorDiscoveryView(string State, long CatalogRevision, IReadOnlyList<EmulatorRoot> Roots,
    IReadOnlyList<EmulatorTargetOption> Targets, IReadOnlyList<EmulatorDiagnostic> Diagnostics);
internal sealed record EmulatorPlanSource(EmulatorTargetFrozen Target, ExecutionPlanExtensionLocksItem Lock);

[SupportedOSPlatform("windows")]
internal sealed class EmulatorTargetService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, ResourceLeaseManager resources, PayloadApplyRoots payloadRoots, ExtensionActivator activator,
    Func<IEnumerable<string>>? registry = null, string? sharedDataRoot = null)
{
    private readonly EmulatorInstalledReader _installed = new(instance, database, contracts, payloadRoots, activator);

    internal void RequireAndroid(AutomationScript script) => _installed.RequireAndroid(script);

    internal async ValueTask<EmulatorDiscoveryView> DiscoverAsync(Guid scriptId, long revision, IReadOnlyList<Guid> rootIds,
        Action authorize, CancellationToken cancellation)
    {
        var catalog = ReadCatalog(revision); var script = catalog.Scripts.SingleOrDefault(value => value.Id == scriptId)
            ?? throw new IOException("emulator_script_missing");
        _installed.RequireAndroid(script);
        var selected = rootIds.Select(id => catalog.Roots.SingleOrDefault(value => value.Id == id)
            ?? throw new IOException("emulator_root_missing")).ToArray();
        var providers = activator.Emulators.List();
        if (providers.Count == 0) throw new IOException("emulator_provider_unavailable");
        var views = new List<EmulatorDiscoveryView>();
        foreach (var owner in providers)
        {
            var roots = ReadRoots(selected, owner);
            views.Add(await UseAsync(roots, authorize, (provider, inventory, discovery, current) =>
            {
            var targets = new List<EmulatorTargetOption>(); var diagnostics = discovery.Diagnostics.ToList();
            foreach (var source in discovery.Sources)
                foreach (var candidate in source.Enumeration.Instances)
                {
                    if (candidate.CandidatePorts.Count == 0) diagnostics.Add(new(source.Installation.Vendor, source.Installation.Id, "emulator_endpoint_unavailable"));
                    foreach (var port in candidate.CandidatePorts.Distinct().Order())
                    {
                        try
                        {
                            var target = provider.Select(discovery.Sources, candidate.TargetId ?? throw new IOException("emulator_target_missing"), "127.0.0.1:" + port, true);
                            var root = RootFor(roots, target.Installation.Adb?.Path ?? throw new IOException("emulator_bundled_adb_required"));
                            var directory = inventory.InstanceDirectory(target, provider.Describe(target));
                            targets.Add(new(Selection(target, root.Id, directory, owner), candidate.DisplayName, target.Endpoint,
                                new(root.Id, Path.GetRelativePath(root.Path, target.Installation.Adb!.Path).Replace('\\', '/'))));
                        }
                        catch (Exception error) when (error is EmulatorException or IOException or ArgumentException)
                        { diagnostics.Add(new(source.Installation.Vendor, source.Installation.Id, error is EmulatorException refused ? refused.Code :
                            error.Message.StartsWith("emulator_", StringComparison.Ordinal) && error.Message.All(value => char.IsAsciiLetterOrDigit(value) || value == '_')
                                ? error.Message : "emulator_target_unavailable")); }
                    }
                }
            current(); _ = ReadCatalog(revision);
            return ValueTask.FromResult(new EmulatorDiscoveryView(discovery.State, revision, roots, targets, diagnostics.Distinct().ToArray()));
            }, cancellation, owner).ConfigureAwait(false));
        }
        return new(views.All(view => view.State == "ready") ? "ready" : "partial", revision,
            views.SelectMany(view => view.Roots).DistinctBy(root => root.Id).ToArray(),
            views.SelectMany(view => view.Targets).ToArray(), views.SelectMany(view => view.Diagnostics).ToArray());
    }

    internal EmulatorPlanSource Capture(AutomationScript script, IReadOnlyList<AutomationRoot> roots)
    {
        _installed.RequireAndroid(script);
        var android = script.Target?.Android ?? throw new IOException("emulator_android_required");
        var expected = android.Emulator ?? throw new IOException("emulator_selection_required");
        var root = roots.Single(value => value.Id == expected.RootId);
        var declared = new EmulatorRoot(root.Id, root.Name, root.Path, true);
        var owner = activator.Emulators.Select(expected.ExtensionId, expected.ProviderId);
        var all = ReadRoots([root], owner);
        return UseAsync(all, () => lease.VerifyHeld(instance), (provider, inventory, discovery, current) =>
        {
            var target = provider.Select(discovery.Sources, expected.TargetId, ExecutionAndroidCommands.Endpoint(android.Endpoint), true);
            var directory = inventory.InstanceDirectory(target, provider.Describe(target)); var selection = Selection(target, declared.Id, directory, owner);
            if (selection.Vendor != expected.Vendor || selection.RootId != expected.RootId.ToString("D") || selection.Generation != expected.Generation ||
                target.Installation.Adb is null || PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(root.Path, android.Tool.RelativePath)) != target.Installation.Adb.Path)
                throw new IOException("emulator_selection_changed");
            if (expected.ExtensionId is null) selection = selection with { ExtensionId = null, ProviderId = null };
            var installed = _installed.Read(owner.ExtensionId, "emulator.provide");
            var blobs = new ImmutableBlobStore(instance, contracts); var manifest = blobs.PersistContent(installed.Bytes, default);
            var identity = PhysicalObjectIdentity.Read(directory);
            var extensionLock = new ExecutionPlanExtensionLocksItem { ExtensionId = installed.Installed.ExtensionId, PackageVersion = installed.Installed.Package.PackageVersion,
                SdkVersion = installed.Manifest.SdkVersion, ContractFingerprint = installed.Manifest.ContractFingerprint,
                ManifestSha256 = manifest.Sha256, PackageSha256 = installed.Installed.Package.ArchiveSha256 };
            var observations = inventory.ReadEvidence().SelectMany(value => new[] { blobs.PersistContent(value.StandardOutput, default),
                blobs.PersistContent(value.StandardError, default) }).Distinct().ToArray();
            var proof = new EmulatorSourceProof("nexus.emulator.source", 2, selection, target.Installation.Id,
                target.Instance.Identity, target.Instance.ControlName, target.Instance.Index, directory,
                new() { VolumeSerial = identity.VolumeSerial, FileId = identity.FileId },
                ExecutionHookImages.Capture(target.Installation.Control.Path, true), ExecutionHookImages.Capture(target.Installation.Adb.Path, true), target.Endpoint,
                new() { RelativePath = manifest.RelativePath, SizeBytes = manifest.SizeBytes, Sha256 = manifest.Sha256 }, extensionLock,
                installed.Activation.ToString("D"), installed.Installed.Revision, observations)
                { ProviderId = owner.ProviderId,
                    Launch = EmulatorTargetProof.Command(provider.Describe(target).Launch, target),
                    Quit = provider.Describe(target).Quit is { } quit ? EmulatorTargetProof.Command(quit, target) : null };
            current(); _installed.Require(installed);
            return ValueTask.FromResult(new EmulatorPlanSource(EmulatorTargetProof.Freeze(proof, blobs), extensionLock));
        }, default, owner).AsTask().GetAwaiter().GetResult();
    }

    internal void RequireCurrent(EmulatorTargetFrozen target, AutomationScript script)
    {
        lease.VerifyHeld(instance); _installed.RequireAndroid(script);
        var installed = _installed.Read(target.ExtensionId, "emulator.provide");
        if (installed.Installed.Revision != target.InstalledRevision || installed.Installed.Package.ArchiveSha256 != target.PackageSha256 ||
            installed.Activation.ToString("D") != target.ActivationId) throw new IOException("emulator_activation_changed");
        EmulatorTargetProof.RequireCurrent(target);
    }

    internal void RequireRuntime(ExecutionTargetAndroid android, Guid scriptId) => RequireRuntimeAsync(android, scriptId, default).AsTask().GetAwaiter().GetResult();

    internal async ValueTask RequireRuntimeAsync(ExecutionTargetAndroid android, Guid scriptId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var frozen = android.Emulator ?? throw new IOException("emulator_selection_required");
        var catalog = new AutomationDefinitionStore(database).Read();
        var script = catalog.Scripts.SingleOrDefault(value => value.Id == scriptId) ?? throw new IOException("emulator_script_missing");
        RequireCurrent(frozen, script);
        var root = catalog.Roots.SingleOrDefault(value => value.Id.ToString("D") == frozen.Selection.RootId) ?? throw new IOException("emulator_root_missing");
        var owner = activator.Emulators.Select(frozen.ExtensionId, frozen.ProviderId);
        _ = await UseAsync(ReadRoots([root], owner), () => lease.VerifyHeld(instance), (provider, inventory, found, current) =>
        {
            var target = provider.Select(found.Sources, frozen.Selection.TargetId, android.Endpoint, true);
            var directory = inventory.InstanceDirectory(target, provider.Describe(target));
            var actual = Selection(target, root.Id, directory, owner);
            if (frozen.Selection.ExtensionId is null) actual = actual with { ExtensionId = null, ProviderId = null };
            if (actual != frozen.Selection || target.Installation.Id != frozen.InstallationId ||
                target.Installation.Adb?.Path != android.Tool.Path || target.Installation.Adb.Sha256 != android.Tool.Sha256 ||
                target.Installation.Control.Path != frozen.Control.Path || target.Installation.Control.Sha256 != frozen.Control.Sha256)
                throw new IOException("emulator_mapping_changed");
            var description = provider.Describe(target);
            if (!EmulatorTargetProof.SameCommand(frozen.Launch, EmulatorTargetProof.Command(description.Launch, target)) ||
                (description.Quit is null ? frozen.Quit is not null : frozen.Quit is null ||
                    !EmulatorTargetProof.SameCommand(frozen.Quit, EmulatorTargetProof.Command(description.Quit, target))))
                throw new IOException("emulator_command_changed");
            current(); return ValueTask.FromResult(true);
        }, cancellation, owner).ConfigureAwait(false);
    }

    internal void RequireActivation(EmulatorTargetFrozen target, Guid scriptId)
    {
        var script = new AutomationDefinitionStore(database).Read().Scripts.SingleOrDefault(value => value.Id == scriptId)
            ?? throw new IOException("emulator_script_missing");
        RequireCurrent(target, script);
    }

    private async ValueTask<T> UseAsync<T>(IReadOnlyList<EmulatorRoot> roots, Action authorize,
        Func<IEmulatorProvider, EmulatorInventoryScope, EmulatorDiscoveryResult, Action, ValueTask<T>> callback, CancellationToken cancellation, (string ExtensionId, string ProviderId) selected)
    {
        var installed = _installed.Read(selected.ExtensionId, "emulator.provide");
        return await activator.Emulators.UseAsync(selected.ExtensionId, selected.ProviderId, async (provider, active) =>
        {
            void Current() { authorize(); active(); _installed.Require(installed); }
            Current();
            await using var inventory = EmulatorInventoryScope.Prepare(instance, lease, resources, roots.Select(value => value.Path).ToArray(), Current, TimeSpan.FromMinutes(1));
            var found = await provider.DiscoverAsync(inventory, true, cancellation).ConfigureAwait(false);
            return await callback(provider, inventory, found, Current).ConfigureAwait(false);
        }, cancellation).ConfigureAwait(false);
    }

    private IReadOnlyList<EmulatorRoot> ReadRoots(IReadOnlyList<AutomationRoot> roots, (string ExtensionId, string ProviderId) selected)
    {
        var hints = activator.Emulators.UseAsync(selected.ExtensionId, selected.ProviderId,
            (provider, current) => { current(); return ValueTask.FromResult(provider.Layouts.SelectMany(layout => layout.InstallHints).Distinct().ToArray()); },
            default).AsTask().GetAwaiter().GetResult();
        if (hints.Length is < 1 or > 64 || hints.Any(value => value.Length is < 1 or > 128 || value.Any(char.IsControl)))
            throw new IOException("emulator_install_hints_invalid");
        return EmulatorRoots.Read(roots, registry, sharedDataRoot, hints);
    }

    private AutomationCatalog ReadCatalog(long revision)
    {
        lease.VerifyHeld(instance); var catalog = new AutomationDefinitionStore(database).Read();
        if (catalog.Revision != revision) throw new IOException("emulator_catalog_revision_conflict"); return catalog;
    }
    private static EmulatorRoot RootFor(IReadOnlyList<EmulatorRoot> roots, string path) => roots.Where(root =>
        path.StartsWith(root.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(root => root.Path.Length).FirstOrDefault() ?? throw new IOException("emulator_root_unbound");
    private static EmulatorTargetSelection Selection(EmulatorTarget target, Guid root, string directory, (string ExtensionId, string ProviderId) owner) => new()
    { Vendor = EmulatorTargetProof.Vendor(target.Installation.Vendor), RootId = root.ToString("D"), TargetId = target.Id,
        Generation = EmulatorTargetProof.Generation(target, PhysicalObjectIdentity.Read(directory)), ExtensionId = owner.ExtensionId, ProviderId = owner.ProviderId };
}
