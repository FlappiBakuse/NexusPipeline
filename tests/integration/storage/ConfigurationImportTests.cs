using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Control.Mcp;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ConfigurationImportTests
{
    [Fact]
    public void AppliesControlPermissions()
    {
        using var fixture = new Fixture(false);
        var imports = new ImportUseCases(fixture.Service(), new());
        var tools = new McpConfigurationTools(new ValueUseCases(new(fixture.Base.Instance, fixture.Base.Database, new(),
            fixture.Lease, fixture.Resources)), imports);
        var parameters = System.Text.Json.JsonSerializer.SerializeToElement(new { name = "configuration_import", arguments = new
        {
            format = "nexus.control.configuration-import", version = 1, operationId = fixture.Request.OperationId,
            bindingId = fixture.Request.BindingId, expectedBindingRevision = 1, expectedScriptRevision = 2,
            resources = fixture.Request.Resources.Select(item => new { resourceId = item.ResourceId, expectedRevision = 0 })
        } });
        var mcp = new AuthenticatedControlRequest(ControlOrigin.Mcp, "fixture", null, () => new(true, true));
        Assert.DoesNotContain(tools.List(mcp), tool => tool.Name == "configuration_import");
        Assert.Throws<UnauthorizedAccessException>(() => tools.Call(mcp, parameters));
        Assert.Equal(0, fixture.Base.Count("configuration_batch_items"));
        var local = new AuthenticatedControlRequest(ControlOrigin.Local, "fixture", null, () => new(true, true));
        Assert.Contains(tools.List(local), tool => tool.Name == "configuration_import");
        _ = tools.Call(local, parameters);
        Assert.Equal(2, fixture.Base.Count("configuration_batch_items"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturesDeclaredSources(bool specialized)
    {
        using var fixture = new Fixture(specialized);
        var before = fixture.Fingerprint();
        var observer = new Observer((point, _) =>
        {
            if (point != BatchPersistencePoint.Prepared) return;
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.First, "changed"));
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.Second, "changed"));
        });
        var result = fixture.Service(observer).Import("fixture", fixture.Request, DateTimeOffset.UtcNow);
        Assert.True(result.IsNew); Assert.Equal(2, result.Items.Length);
        Assert.All(result.Items, item => Assert.Equal(1, item.Saved.Revision));
        Assert.Equal(before, fixture.Fingerprint());
        var files = result.Items.SelectMany(item => item.Saved.Tree.Entries).Where(entry => entry.Kind == FileObjectKind.File).ToArray();
        Assert.Equal(2, files.Length);
        foreach (var entry in files)
        {
            using var stream = new ImmutableBlobStore(fixture.Base.Instance, new()).OpenVerified(new(
                $"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
            using var reader = new StreamReader(stream); Assert.Contains("fixture-private", reader.ReadToEnd());
        }
        ConfigurationBatchInventory.VerifyReadOnly(fixture.Base.Instance, fixture.Base.Database, new());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplaysCapturedSources(bool specialized)
    {
        using var fixture = new Fixture(specialized);
        var service = fixture.Service();
        var saved = service.Import("fixture", fixture.Request, DateTimeOffset.UtcNow);
        File.Delete(fixture.First); File.Delete(fixture.Second);
        var definitions = new AutomationDefinitionStore(fixture.Base.Database); var catalog = definitions.Read();
        var binding = catalog.Bindings.Single(item => item.Id == fixture.Request.BindingId);
        var change = new AutomationChanges(catalog.Revision, [], [], [], [binding with { Priority = 10 }], [], [], []);
        definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
        var replay = service.Import("fixture", fixture.Request, DateTimeOffset.UtcNow);
        Assert.False(replay.IsNew);
        Assert.Equal(saved.Items.Select(item => item.Saved.Tree.Entries.ToArray()), replay.Items.Select(item => item.Saved.Tree.Entries.ToArray()));
        Assert.Throws<OperationConflictException>(() => service.Import("foreign", fixture.Request, DateTimeOffset.UtcNow));
        Assert.Throws<OperationConflictException>(() => service.Import("fixture", fixture.Request with { ExpectedScriptRevision = 3 }, DateTimeOffset.UtcNow));
        Assert.Throws<ConfigurationException>(() => service.Import("fixture", fixture.Request with { OperationId = Guid.NewGuid() }, DateTimeOffset.UtcNow));
        Assert.Equal(2, fixture.Base.Count("configuration_batch_items"));
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("script")]
    [InlineData("resource")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("cancelled")]
    [InlineData("logical")]
    [InlineData("physical")]
    public void RejectsUnsafeImports(string reason)
    {
        using var fixture = new Fixture(false);
        var request = fixture.Request;
        if (reason == "binding") request = request with { ExpectedBindingRevision = 2 };
        if (reason == "script") request = request with { ExpectedScriptRevision = 3 };
        if (reason == "resource") request = request with { Resources = [new("undeclared", 0)] };
        if (reason == "duplicate") request = request with { Resources = [request.Resources[0], request.Resources[0]] };
        if (reason == "missing") { File.Delete(fixture.Second); Directory.Delete(Path.GetDirectoryName(fixture.Second)!); }
        var before = fixture.Fingerprint();
        using var held = reason == "logical" ? fixture.Resources.TryAcquire(Guid.NewGuid(),
            [new("automation:definitions", ResourceClaimMode.Mutation)]).Lease : reason == "physical" ?
            fixture.Resources.TryAcquire(Guid.NewGuid(), PhysicalResourceAdmission.Read(fixture.First)).Lease : null;
        var cancellation = reason == "cancelled" ? new CancellationToken(true) : default;
        if (reason == "cancelled") Assert.Throws<OperationCanceledException>(() => fixture.Service().Import("fixture", request, DateTimeOffset.UtcNow, cancellation));
        else Assert.Throws<ConfigurationException>(() => fixture.Service().Import("fixture", request, DateTimeOffset.UtcNow));
        Assert.Equal(before, fixture.Fingerprint()); Assert.Equal(0, fixture.Base.Count("configuration_batch_items"));
    }

    [Fact]
    public void RollsBackImport()
    {
        using var fixture = new Fixture(false); var before = fixture.Fingerprint();
        var observer = new Observer((point, index) =>
        { if (point == BatchPersistencePoint.ItemPublished && index == 0) throw new IOException("injected_import_failure"); });
        Assert.Throws<IOException>(() => fixture.Service(observer).Import("fixture", fixture.Request, DateTimeOffset.UtcNow));
        Assert.Equal(0, fixture.Base.Count("configuration_batch_items"));
        foreach (var resource in fixture.Request.Resources) Assert.Null(fixture.Base.Revisions.Read(new(fixture.Request.BindingId, resource.ResourceId)));
        Assert.Equal(before, fixture.Fingerprint());
        Assert.True(fixture.Service().Import("fixture", fixture.Request, DateTimeOffset.UtcNow).IsNew);
        Assert.Throws<ConfigurationException>(() => fixture.Service().Import("fixture",
            fixture.Request with { OperationId = Guid.NewGuid() }, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsLinkedSources(bool broken)
    {
        using var fixture = new Fixture(false);
        var selected = Path.GetDirectoryName(fixture.Second)!;
        var outside = Path.Combine(fixture.Base.Root, "outside-import");
        Directory.CreateDirectory(outside);
        var original = Path.Combine(outside, "second.json");
        File.Move(fixture.Second, original);
        Directory.Delete(selected);
        ControlledJunction.Create(selected, outside);
        try
        {
            if (broken) { File.Delete(original); Directory.Delete(outside); }
            var error = Assert.Throws<IOException>(() => fixture.Service().Import("fixture", fixture.Request, DateTimeOffset.UtcNow));
            Assert.Equal("filesystem_reparse_point_rejected", error.Message);
            Assert.Equal(0, fixture.Base.Count("configuration_batch_items"));
            if (!broken) Assert.Equal("{\"fixture-private\":2}", File.ReadAllText(original));
            foreach (var resource in fixture.Request.Resources)
                Assert.Null(fixture.Base.Revisions.Read(new(fixture.Request.BindingId, resource.ResourceId)));
        }
        finally { Directory.Delete(selected); }
    }

    private sealed class Observer(Action<BatchPersistencePoint, int> action) : IBatchPersistenceObserver
    { public void Reached(BatchPersistencePoint point, Guid operationId, int resourceOrdinal) => action(point, resourceOrdinal); }

    private sealed class Fixture : IDisposable
    {
        internal ConfigurationBatchFixture Base { get; } = new();
        internal InstanceLease Lease { get; }
        internal ResourceLeaseManager Resources { get; }
        internal ConfigurationImportRequest Request { get; }
        internal string First { get; }
        internal string Second { get; }

        internal Fixture(bool specialized)
        {
            var definitions = new AutomationDefinitionStore(Base.Database); var catalog = definitions.Read();
            var root = catalog.Roots[0];
            First = Path.Combine(root.Path, "first.json"); Second = Path.Combine(root.Path, "profile/second.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Second)!);
            File.WriteAllText(First, "{\"fixture-private\":1}"); File.WriteAllText(Second, "{\"fixture-private\":2}");
            var paths = ImmutableArray.Create(new AutomationPath(root.Id, "first.json"), new AutomationPath(root.Id, "profile"));
            var script = catalog.Scripts[0] with { ConfigurationPaths = paths };
            var bindingId = Base.Binding;
            ImmutableArray<AutomationBinding> bindings = [];
            if (specialized) script = script with { Id = Guid.NewGuid(), Revision = 0, Name = "Specialized import", Order = 1,
                Kind = AutomationKind.Specialized, ExtensionId = "fixture-adapter", AdapterResources =
                [new("main", 0, "", 1, ConfigurationSyntax.Json, new([new(new([]), true)], [], [])),
                 new("profile", 1, "second.json", 1, ConfigurationSyntax.Json, new([new(new([]), true)], [], []))] };
            if (specialized)
            {
                bindingId = Guid.NewGuid();
                bindings = [catalog.Bindings[0] with { Id = bindingId, Revision = 0, ScriptId = script.Id, Priority = 2 }];
            }
            var change = new AutomationChanges(catalog.Revision, [], [script], [], bindings, [], [], []);
            definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
            Request = new(Guid.NewGuid(), bindingId, 1, specialized ? 1 : 2, (specialized ? script.AdapterResources.Select(item => item.ResourceId) :
                paths.Select(GenericConfigurationPolicy.ResourceId)).Select(id => new ConfigurationImportResource(id, 0)).ToImmutableArray());
            Lease = InstanceLease.Acquire(Base.Instance, new());
            var recovery = new StartupJournalRecovery(Base.Instance, new(), Base.Database,
                new(Base.Root, Path.Combine(Base.Instance.RuntimeRoot, "extensions"))).Recover(Lease);
            Resources = new(new(recovery, TimeProvider.System));
        }
        internal ConfigurationImportService Service(IBatchPersistenceObserver? observer = null) =>
            new(Base.Instance, Base.Database, new(), Lease, Resources, observer);
        internal string[] Fingerprint() => new[] { First, Second }.Select(path => File.Exists(path) ?
            File.ReadAllText(path) + PhysicalObjectIdentity.Read(path).ToString() : "missing").ToArray();
        public void Dispose() => Lease.Dispose();
    }
}
