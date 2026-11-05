using System.Runtime.Versioning;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CompiledProjectTests
{
    [Fact(DisplayName = "Installed Script Compilation Uses Independent Choices And Shared Preview Lifetime Without Account Execution Authority")]
    public Task EditsScriptProject() => FreezesInstalledProject("script");

    [Fact(DisplayName = "Installed Adapter Projection Creates Actual Frozen Configuration Candidates Without Reading Live Bytes")]
    public Task ProjectsInstalledTasks() => FreezesInstalledProject("projection");

    [Fact(DisplayName = "Actual Installed Adapter Retries Only The Failed Task And Carries Prior Success Through Persisted Evidence")]
    public Task RetriesInstalledTasks() => FreezesInstalledProject("worker-task-retry");

    [Theory(DisplayName = "The Actual Installed Compiler Freezes Saved Configurations Complete Resource Archives And Large Assets Under One Live Publication Scope")]
    [InlineData("normal")]
    [InlineData("overlap")]
    [InlineData("revoked")]
    [InlineData("accepted")]
    [InlineData("worker")]
    [InlineData("worker-peer")]
    [InlineData("worker-sequence")]
    [InlineData("worker-fingerprint")]
    [InlineData("worker-secret")]
    [InlineData("worker-secret-field")]
    [InlineData("worker-secret-task")]
    [InlineData("worker-secret-revision")]
    public async Task FreezesInstalledProject(string mode)
    {
        var worker = mode.StartsWith("worker", StringComparison.Ordinal);
        var protectedWorker = mode.StartsWith("worker-secret", StringComparison.Ordinal);
        var workerMode = mode == "worker" ? "project-read" : mode.Replace("worker-", "project-", StringComparison.Ordinal);
        using var project = new ProjectSourceFixture(configuration: true, enabled: mode is "accepted" or "projection" || worker,
            taskProjection: protectedWorker || mode is "projection" or "worker-task-retry", taskRetry: mode == "worker-task-retry");
        project.Write("interface.json", "{\"task\":[]}"u8.ToArray());
        var assets = Path.Combine(project.Path, "assets"); Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "pipeline.json"), "{\"entry\":{\"action\":\"DoNothing\"}}");
        File.WriteAllBytes(Path.Combine(assets, "image.png"), [1, 2, 3, 4]);
        var providers = new ExecutionProviderRegistry();
        using var provider = new InstallRuntimeFixture(managedMode: "frontend", catalog: project.Catalog, providers: providers,
            managedPayloadEdit: directory => { if (worker) CompiledWorkerFixture.Populate(directory); }, managedManifestEdit: manifest =>
        {
            manifest["capabilities"]!.AsArray().Add("source.compile"); manifest["managed"]!["compilers"] = new JsonArray("project");
            manifest["permissions"]!["fileAccess"]!.AsArray().Add("selected-script-root");
            if (protectedWorker) manifest["permissions"]!["secretAccess"] = new JsonArray("selected-binding");
            if (worker) CompiledWorkerFixture.Declare(manifest, Path.Combine(project.Path, "synthetic-worker-ready"), workerMode);
        });
        File.WriteAllText(Path.Combine(provider.ActivationDirectory, "compiler-mode"), "enabled");
        _ = await FrontendInstalledTests.OpenAsync(provider);
        if (protectedWorker)
        {
            File.WriteAllText(Path.Combine(provider.ActivationDirectory, "compiler-protected"), "enabled");
            using var secret = new SecretValue("PROTECTED_PIPE_FIXTURE_密码🔒");
            _ = await new SecretFieldStore(project.Catalog.Instance, project.Catalog.Database).ApplyAsync(Guid.NewGuid(),
                new("fixture-plugin", project.Binding, "input-fixture"), 0, SecretDisposition.Set, secret, default);
        }
        var manifest = JsonNode.Parse(provider.Package.ManifestBytes)!.AsObject();
        manifest["extensionId"] = "maas"; manifest["artifactName"] = "FixtureConsumer"; manifest["kind"] = "data-specialized";
        manifest.Remove("managed"); manifest.Remove("frontend"); manifest["capabilities"] = new JsonArray("task.adapt", "configuration.edit");
        manifest.Remove("execution");
        manifest["permissions"] = new JsonObject { ["fileAccess"] = new JsonArray("selected-script-root") };
        manifest["specialized"] = new JsonObject { ["adapterEntry"] = "adapter.js", ["compiler"] = new JsonObject
            { ["extensionId"] = "fixture-plugin", ["compilerId"] = "project" } };
        if (worker) manifest["specialized"]!["executionProvider"] = new JsonObject { ["extensionId"] = "fixture-plugin", ["entryId"] = "main" };
        using var consumer = new InstallRuntimeFixture(catalog: project.Catalog, originalManifest: manifest,
            managedPayloadEdit: directory => File.WriteAllText(Path.Combine(directory, "adapter.js"),
                CompiledTaskChecks.Adapter(project)),
            payloadRoots: provider.Roots, existingActivator: provider.Activator, catalogSequence: 2);
        try
        {
            _ = await consumer.Session().ApplyAsync(consumer.Prepared, default);
            consumer.Prepared.Dispose(); consumer.Verified.Dispose(); consumer.Root.Dispose();
            var catalog = project.Catalog;
            ExtensionProjectionPublication.PublishIfDirty(catalog.Instance, catalog.Database, catalog.Lease,
                catalog.Contracts, provider.Roots, catalog.Resources, catalog.Index);
            var service = new ProjectCompilationService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources,
                catalog.Contracts, provider.Roots, provider.Activator, protectedWorker ? new SecretDocumentService(catalog.Instance,
                    catalog.Database, catalog.Lease, catalog.Resources, catalog.Index, catalog.Contracts) : null);
            if (mode == "script") { await ScriptProjectChecks.VerifyAsync(project, provider); return; }
            var valid = true; Action? retained = null; var published = false;
            var selection = JsonSerializer.SerializeToElement(new { mount = true, resourcePath = mode == "overlap" ? "config" : "assets" });
            var running = service.UseAsync(project.Binding, 1, "maas", selection,
                () => { if (!valid) throw new UnauthorizedAccessException("fixture_revoked"); }, async (snapshot, check, live) =>
                {
                    retained = check; check();
                    if (mode == "revoked") { valid = false; check(); }
                    AssertSnapshot(catalog, snapshot); AssertReadBudget(catalog, snapshot, live);
                    await AssertInstalledAdapter(provider, catalog, snapshot, live);
                    published = true; return snapshot.Document.Sha256;
                }, default).AsTask();
            if (mode == "overlap") Assert.Equal("project_resource_configuration_overlap", (await Assert.ThrowsAsync<IOException>(() => running)).Message);
            else if (mode == "revoked") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => running);
            else
            {
                Assert.Equal(64, (await running).Length); await AssertSavedChoice(service, project);
                var adapters = await AssertAdapterService(provider, project);
                if (mode is "accepted" or "projection" || worker) await AssertAcceptedPlan(service, adapters, provider, project, worker ? providers : null, workerMode);
                else
                {
                    await AssertPreviewService(service, provider, project);
                    await ProjectControlChecks.VerifyAsync(service, provider, project);
                }
            }
            Assert.Equal(mode is "normal" or "accepted" or "projection" || worker, published);
            if (retained is not null) Assert.Throws<ObjectDisposedException>(retained);
        }
        finally { await provider.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    private static void AssertSnapshot(ExtensionCatalogFixture catalog, CompiledProjectSnapshot snapshot)
    {
        Assert.Equal(catalog.Instance.InstanceId, snapshot.Value.InstanceId);
        Assert.Equal("fixture-plugin", snapshot.Value.Provider.Installed.ExtensionId); Assert.Equal("maas", snapshot.Value.Consumer.Installed.ExtensionId);
        Assert.Equal(3 * 1024 * 1024, snapshot.Value.Assets.Single(item => item.Name == "artifact").Content.SizeBytes);
        Assert.Single(snapshot.Value.ConfigurationFiles); Assert.Single(snapshot.Value.ResourceTrees);
        Assert.Equal(snapshot.Value.ConfigurationFiles[0].Content.Sha256, snapshot.Value.ExecutionView.GetProperty("configurationSha256").GetString());
        using var document = new ImmutableBlobStore(catalog.Instance, catalog.Contracts).OpenVerified(snapshot.Document);
        using var parsed = JsonDocument.Parse(document);
        Assert.Single(parsed.RootElement.GetProperty("SourceFiles").EnumerateArray());
        Assert.True(snapshot.Closure.Any(item => item.Sha256 == snapshot.Document.Sha256)); Assert.True(snapshot.Closure.Any(item => item.SizeBytes == 4));
        var blobs = new ImmutableBlobStore(catalog.Instance, catalog.Contracts);
        var restored = CompiledProjectCodec.Read(catalog.Instance, blobs, snapshot.Document);
        ProviderProjectChecks.Verify(blobs, restored);
        Assert.Equal(snapshot.Document, restored.Document); Assert.True(snapshot.Closure.SequenceEqual(restored.Closure));
        Assert.True(JsonElement.DeepEquals(snapshot.Value.ExecutionView, restored.Value.ExecutionView));
        var missing = snapshot.Value with { Contents = snapshot.Value.Contents.RemoveAt(snapshot.Value.Contents.Length - 1) };
        var incomplete = blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(missing));
        Assert.Throws<IOException>(() => CompiledProjectCodec.Read(catalog.Instance, blobs, incomplete));
        var foreign = blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(snapshot.Value with { InstanceId = Guid.NewGuid() }));
        Assert.Throws<IOException>(() => CompiledProjectCodec.Read(catalog.Instance, blobs, foreign));
    }

    private static void AssertReadBudget(ExtensionCatalogFixture catalog, CompiledProjectSnapshot snapshot, Action check)
    {
        var blobs = new ImmutableBlobStore(catalog.Instance, catalog.Contracts);
        using var compiled = new CompiledAdapterInput(blobs, snapshot, check);
        Assert.Equal("true", compiled.Read("artifact", "/value/a~1b/~0/1"));
        Assert.Throws<IOException>(() => compiled.Read("artifact", "/value/a~2b"));
        Assert.Throws<IOException>(() => compiled.Read("../../manifest.json", ""));
        Assert.Throws<JsonException>(() => compiled.Read("artifact", "/padding"));
        using var calls = new CompiledAdapterInput(blobs, snapshot, check);
        for (var index = 0; index < 128; index++) Assert.Equal("{}", calls.Read("index", ""));
        Assert.Throws<IOException>(() => calls.Read("index", ""));
        using var bytes = new CompiledAdapterInput(blobs, snapshot, check);
        for (var index = 0; index < 20; index++) Assert.Equal(100002, bytes.Read("artifact", "/chunk").Length);
        Assert.Throws<IOException>(() => bytes.Read("artifact", "/chunk"));
    }

    private static async Task AssertInstalledAdapter(InstallRuntimeFixture provider, ExtensionCatalogFixture catalog,
        CompiledProjectSnapshot snapshot, Action check)
    {
        using var compiled = new CompiledAdapterInput(new(catalog.Instance, catalog.Contracts), snapshot, check);
        using var use = catalog.Resources.TryAcquire(Guid.NewGuid(),
            [new("extension:maas", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference)]).Lease!;
        var identity = new TaskMessageIdentity("maas", use.OperationId, "discover", null, null);
        var request = new TaskAdapter { Format = "nexus.task.adapter", Version = 1, Direction = "request", Phase = "discover",
            ExtensionId = "maas", OperationId = use.OperationId.ToString("D"), Resources = [], Compiled = compiled.Wire };
        var wire = JsonSerializer.SerializeToUtf8Bytes(request);
        var response = await provider.Activator.RequirePublishedRuntime("maas").InvokeAdapterAsync(use, check, (executor, current) =>
        {
            current(); Assert.Throws<UnauthorizedAccessException>(() => executor.Execute(wire, identity, default));
            var result = executor.Execute(wire, identity, default, compiled);
            var forged = request with { Compiled = request.Compiled! with { Fingerprint = new string('a', 64) } };
            Assert.Throws<UnauthorizedAccessException>(() => executor.Execute(JsonSerializer.SerializeToUtf8Bytes(forged), identity, default, compiled));
            return ValueTask.FromResult(result);
        });
        Assert.Empty(response.Tasks!);
        compiled.Dispose(); Assert.Throws<ObjectDisposedException>(() => compiled.Read("artifact", "/value"));
    }

    private static async Task AssertSavedChoice(ProjectCompilationService service, ProjectSourceFixture project)
    {
        var operation = Guid.NewGuid(); var choice = JsonSerializer.SerializeToElement(new { mount = true, resourcePath = "assets" });
        var saved = await service.SaveAsync(operation, "fixture", project.Binding, 1, 0, "maas", choice, () => { }, DateTimeOffset.UtcNow, default);
        Assert.Equal(1, saved.Saved.Revision); Assert.Equal(ProjectChoiceStore.Address(project.Binding), saved.Saved.Address);
        var replay = await service.SaveAsync(operation, "fixture", project.Binding, 1, 0, "maas", choice, () => { }, DateTimeOffset.UtcNow, default);
        Assert.False(replay.IsNew); Assert.Equal(saved.Saved.Revision, replay.Saved.Revision);
        await Assert.ThrowsAsync<OperationConflictException>(() => service.SaveAsync(operation, "fixture", project.Binding, 1, 0, "maas",
            JsonSerializer.SerializeToElement(new { mount = false }), () => { }, DateTimeOffset.UtcNow, default).AsTask());
        File.WriteAllText(project.ConfigurationFile!, "{\"account\":\"another-live-account\"}");
        var first = await service.UseSavedAsync(project.Binding, 1, "maas", () => { }, async (snapshot, current, live) =>
        {
            current(); live(); Assert.NotNull(snapshot.Value.Choice);
            Assert.Equal(1, snapshot.Value.Choice!.Source.ExpectedRevision);
            Assert.Contains(snapshot.Value.Choice.Source, snapshot.Value.ReadSet);
            Assert.Contains(snapshot.Value.Choice.Snapshot, snapshot.Closure);
            var restored = CompiledProjectCodec.Read(project.Catalog.Instance, new(project.Catalog.Instance, new()), snapshot.Document);
            Assert.Equal(snapshot.Value.Choice, restored.Value.Choice);
            await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(Guid.NewGuid(), "fixture", project.Binding, 1, 1, "maas", choice,
                () => { }, DateTimeOffset.UtcNow, default).AsTask());
            return snapshot.Document.Sha256;
        }, default);
        var second = await service.UseSavedAsync(project.Binding, 1, "maas", () => { }, (snapshot, current, _) =>
        { current(); return ValueTask.FromResult(snapshot.Document.Sha256); }, default);
        Assert.Equal(first, second);
        var next = await service.SaveAsync(Guid.NewGuid(), "fixture", project.Binding, 1, 1, "maas", choice, () => { }, DateTimeOffset.UtcNow, default);
        Assert.Equal(2, next.Saved.Revision);
        var revised = await service.UseSavedAsync(project.Binding, 1, "maas", () => { }, (snapshot, current, _) =>
        { current(); Assert.Equal(2, snapshot.Value.Choice!.Source.ExpectedRevision); return ValueTask.FromResult(snapshot.Document.Sha256); }, default);
        Assert.NotEqual(first, revised);
        await Assert.ThrowsAsync<IOException>(() => service.UseSavedAsync(project.Binding, 2, "maas", () => { },
            (_, _, _) => ValueTask.FromResult(true), default).AsTask());
    }

    private static async Task<TaskAdapterService> AssertAdapterService(InstallRuntimeFixture provider, ProjectSourceFixture project)
    {
        var catalog = project.Catalog;
        var installed = new ExtensionInstalledEvidence(catalog.Instance, catalog.Database, new(), provider.Roots).ReadCurrent("maas")!;
        var adapters = new TaskAdapterService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources, new(), provider.Roots,
            provider.Activator, new(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources));
        var selection = new AdapterSelection(Guid.NewGuid(), project.Binding, 1, "maas", installed.Metadata.Revision,
            [new("account", 1, "local.json")]);
        var discovery = await adapters.DiscoverAsync("fixture", selection, default);
        Assert.Equal(project.HasTaskProjection ? 2 : 0, discovery.Tasks.Length); Assert.Equal(64, discovery.Source.CompilationFingerprint!.Length);
        var proposal = await adapters.ProposeConfigurationAsync("fixture", selection, default);
        Assert.Empty(proposal.Patches); Assert.Equal(discovery.Source.CompilationFingerprint, proposal.Source.CompilationFingerprint);
        var definitions = new AutomationDefinitionStore(catalog.Database).Read(); var settings = new HostSettingsStore(catalog.Database).Read();
        var selected = ExecutionSelectionPolicy.Select(definitions, settings, new(new BindingTarget(project.Binding), definitions.Revision, settings.Revision));
        var captured = adapters.CaptureForPlan(Assert.Single(selected.Items));
        Assert.Equal("fixture-plugin", Assert.Single(captured.CompilerLocks).ExtensionId);
        Assert.Equal(captured.Discovery.Source.CompilationFingerprint, captured.Descriptor.Compiled!.Sha256);
        Assert.Contains(ProjectChoiceStore.ResourceId, captured.Descriptor.CompiledResources!);
        AssertPlanBinding(project, selected, captured);
        var started = Path.Combine(provider.ActivationDirectory, "compiler-started"); File.Delete(started);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapters.DiscoverAsync("fixture", selection, default,
            () => { if (File.Exists(started)) throw new UnauthorizedAccessException("fixture_permission_revoked"); }).AsTask());
        Assert.True(File.Exists(started));
        return adapters;
    }

    private static async Task AssertAcceptedPlan(ProjectCompilationService compilation, TaskAdapterService adapters,
        InstallRuntimeFixture provider, ProjectSourceFixture project, ExecutionProviderRegistry? providers = null, string workerMode = "project-read")
    {
        var catalog = project.Catalog; var inbox = new ExecutionAdmissionInbox();
        var service = new ExecutionAdmissionService(catalog.Instance, catalog.Database, new(), catalog.Lease, catalog.Resources, catalog.Index,
            inbox, new(5, 6, 7), payloadRoots: provider.Roots, providers: providers);
        service.ConfigureTaskAdapters(adapters);
        var definitions = new AutomationDefinitionStore(catalog.Database).Read(); var settings = new HostSettingsStore(catalog.Database).Read();
        var request = new ExecutionSelectionRequest(new BindingTarget(project.Binding), definitions.Revision, settings.Revision);
        var operation = Guid.NewGuid(); var result = service.Start(operation, "fixture", request, DateTimeOffset.UtcNow);
        if (providers is not null)
        {
            await CompiledWorkerFixture.RunAsync(catalog, inbox, adapters, project, result.JournalId, workerMode);
            return;
        }
        using var accepted = Assert.IsType<AcceptedExecutionAdmission>(inbox.Take(operation));
        Assert.Equal(operation, accepted.Identity.OperationId); Assert.Equal(result.JournalId, accepted.Receipt.JournalId);
        var plan = accepted.Prepared.Total.Plan.ReadWire(); var step = Assert.Single(plan.Steps);
        if (project.HasTaskProjection) CompiledTaskChecks.Verify(catalog, accepted, project, step);
        Assert.Equal("fixture-plugin", step.Adapter!.CompiledProvider);
        Assert.Equal("maas", step.ExtensionId);
        Assert.Equal(definitions.Scripts.Single(item => item.Id.ToString("D") == step.ScriptId).UpdateConfigurationAtFinish, step.UpdateConfigurationAtFinish);
        Assert.Contains("extension:fixture-plugin", plan.ResourceKeys);
        Assert.Contains(ConfigurationRevisionService.ResourceKey(ProjectChoiceStore.Address(project.Binding)), plan.ResourceKeys);
        var source = step.Adapter.Compiled!; var blobs = new ImmutableBlobStore(catalog.Instance, new());
        var snapshot = CompiledProjectCodec.Read(catalog.Instance, blobs, new(source.RelativePath, source.SizeBytes, source.Sha256));
        Assert.True(snapshot.Value.CanExecute); Assert.Equal(2, snapshot.Value.Choice!.Source.ExpectedRevision);
        var choice = JsonSerializer.SerializeToElement(new { mount = true, resourcePath = "assets" });
        await Assert.ThrowsAsync<IOException>(() => compilation.SaveAsync(Guid.NewGuid(), "fixture", project.Binding, 1, 2, "maas", choice,
            () => { }, DateTimeOffset.UtcNow, default).AsTask());
    }

    private static async Task AssertPreviewService(ProjectCompilationService compilation, InstallRuntimeFixture provider, ProjectSourceFixture project)
    {
        var catalog = project.Catalog; var clock = new PreviewClock();
        await using var previews = new ProjectPreviewService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources,
            new(), provider.Roots, provider.Activator, clock);
        var discovery = await compilation.DiscoverAsync(project.Binding, 1, "maas", JsonSerializer.SerializeToElement(new { }), () => { }, default);
        Assert.True(discovery.GetProperty("Exists").GetBoolean());
        var first = await previews.PrepareAsync(compilation, "fixture", project.Binding, 1, "maas", () => { }, default);
        Assert.Equal("index", first.RootIndex); Assert.True(first.CanExecute);
        var page = previews.Read("fixture", first.PreviewId, "index", () => { });
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(page.ContentBase64)));
        Assert.Equal(first.Assets.Single(item => item.Name == "index").Sha256, page.Sha256);
        Assert.Throws<UnauthorizedAccessException>(() => previews.Read("another-subject", first.PreviewId, "index", () => { }));
        Assert.Throws<UnauthorizedAccessException>(() => previews.Read("fixture", first.PreviewId, "artifact", () => { }));
        Assert.Throws<UnauthorizedAccessException>(() => previews.Read("fixture", first.PreviewId, "../interface.json", () => { }));
        Assert.Single(first.Assets);
        Assert.Throws<UnauthorizedAccessException>(() => previews.Read("fixture", first.PreviewId, "root", () => { }));
        Assert.Throws<UnauthorizedAccessException>(() => previews.Read("fixture", first.PreviewId, "index", () => throw new UnauthorizedAccessException("fixture_revoked")));
        var second = await previews.PrepareAsync(compilation, "fixture", project.Binding, 1, "maas", () => { }, default);
        Assert.NotEqual(first.PreviewId, second.PreviewId);
        Assert.Throws<IOException>(() => previews.Read("fixture", first.PreviewId, "index", () => { }));
        Assert.Throws<UnauthorizedAccessException>(() => previews.Release("another-subject", second.PreviewId));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Throws<IOException>(() => previews.Read("fixture", second.PreviewId, "index", () => { }));
        Assert.False(previews.Release("fixture", second.PreviewId));
        using var owner = new CancellationTokenSource();
        var owned = await previews.PrepareAsync(compilation, "fixture", project.Binding, 1, "maas", () => { }, default, owner.Token);
        owner.Cancel();
        Assert.Throws<IOException>(() => previews.Read("fixture", owned.PreviewId, "index", () => { }));
        Assert.False(previews.Release("fixture", owned.PreviewId));
        var third = await previews.PrepareAsync(compilation, "fixture", project.Binding, 1, "maas", () => { }, default);
        var selection = JsonSerializer.SerializeToElement(new { mount = true, resourcePath = "assets" });
        _ = await compilation.SaveAsync(Guid.NewGuid(), "fixture", project.Binding, 1, 2, "maas", selection, () => { }, DateTimeOffset.UtcNow, default);
        Assert.Throws<NexusPipeline.Core.Configuration.ConfigurationException>(() => previews.Read("fixture", third.PreviewId, "index", () => { }));
        Assert.True(previews.Release("fixture", third.PreviewId)); Assert.False(previews.Release("fixture", third.PreviewId));
    }

    private sealed class PreviewClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan amount) => _now += amount;
    }

    private static void AssertPlanBinding(ProjectSourceFixture project, ExecutionSelection selection, AdapterPlanSource captured)
    {
        var catalog = project.Catalog; var blobs = new ImmutableBlobStore(catalog.Instance, new());
        var reference = captured.Descriptor.Compiled!;
        var snapshot = CompiledProjectCodec.Read(catalog.Instance, blobs, new(reference.RelativePath, reference.SizeBytes, reference.Sha256));
        var item = selection.Items.Single(); var template = ExecutionPlanFixture.Create().Plan;
        var step = template.Steps[0] with { ScriptId = item.Script.Id.ToString("D"), UserId = item.User.Id.ToString("D"),
            BindingId = project.Binding.ToString("D"), ExtensionId = "maas", Adapter = captured.Descriptor };
        var plan = template with { InstanceId = catalog.Instance.InstanceId.ToString("D"), Steps = [step],
            ExtensionLocks = captured.CompilerLocks.Prepend(captured.Lock).ToArray(),
            SourceRevisions = selection.SourceRevisions.Select(value => new ExecutionPlanSourceRevisionsItem
                { EntityKind = value.Kind.ToString().ToLowerInvariant(), EntityId = value.EntityId.ToString("D"), Revision = value.Revision }).ToArray(),
            RootBindings = [new ExecutionPlanRootBindingsItem { RootId = item.Script.RootId.ToString("D"), Path = project.Path,
                Identity = new() { VolumeSerial = snapshot.Value.RootIdentity.VolumeSerial, FileId = snapshot.Value.RootIdentity.FileId } }] };
        using var original = blobs.OpenVerified(snapshot.Value.Consumer.Manifest);
        var bytes = new byte[checked((int)original.Length)]; original.ReadExactly(bytes);
        var manifest = new ExtensionManifestAdmission(new()).ReadCurrent(bytes, "maas");
        var restored = ExecutionCompilationBinding.Require(plan, step, item.Script.RootId.ToString("D"), manifest, blobs, new(), false)!;
        Assert.Equal(snapshot.Document, restored.Document);
        Assert.True(snapshot.Closure.ToHashSet().SetEquals(ExecutionCompilationBinding.References(plan, blobs)));
        Assert.Throws<IOException>(() => ExecutionCompilationBinding.Require(plan with { ExtensionLocks = [captured.Lock] }, step,
            item.Script.RootId.ToString("D"), manifest, blobs, new(), false));
        Assert.Throws<IOException>(() => ExecutionCompilationBinding.Require(plan, step with { Adapter = captured.Descriptor with { CompiledResources = ["account"] } },
            item.Script.RootId.ToString("D"), manifest, blobs, new(), false));
        var blocked = blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(snapshot.Value with { CanExecute = false }));
        var denied = step with { Adapter = captured.Descriptor with { Compiled = new() { RelativePath = blocked.RelativePath, SizeBytes = blocked.SizeBytes, Sha256 = blocked.Sha256 } } };
        Assert.Equal("execution_compilation_readonly", Assert.Throws<IOException>(() => ExecutionCompilationBinding.Require(plan, denied,
            item.Script.RootId.ToString("D"), manifest, blobs, new(), false)).Message);
    }
}
