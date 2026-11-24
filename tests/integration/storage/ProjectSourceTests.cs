using System.Security.Cryptography;
using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ProjectSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsExactMetadata(bool guiAssets)
    {
        using var fixture = new ProjectSourceFixture();
        fixture.Write("interface.json", "{\"name\":\"MaaStellaSora\"}"u8.ToArray());
        var directory = Path.Combine(fixture.Path, guiAssets ? "Assets" : "assets");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "interface.json"), "{\"name\":\"nested\"}");
        using var source = fixture.Open();
        Assert.Contains("MaaStellaSora", System.Text.Encoding.UTF8.GetString(source.ReadMetadata("interface.json", default)!.Content.Span));
        var nested = source.ReadMetadata("assets/interface.json", default);
        if (guiAssets)
        {
            Assert.Null(nested);
            Assert.Throws<IOException>(() => source.Read("assets/interface.json", default));
        }
        else Assert.Contains("nested", System.Text.Encoding.UTF8.GetString(nested!.Content.Span));
        source.RequireCurrent();
        var captured = source.Freeze(default);
        Assert.Equal(guiAssets ? 1 : 2, captured.Files.Length);
    }

    [Fact]
    public void HoldsDraftMounts()
    {
        using var fixture = new ProjectSourceFixture(configuration: true, file: true);
        var host = fixture.Catalog;
        var script = Assert.Single(new AutomationDefinitionStore(host.Database).Read().Scripts);
        var binding = Guid.NewGuid();
        var owner = new DraftBindingIdentity(Guid.NewGuid(), script.Id, script.Revision);
        var address = new ConfigurationAddress(binding, "account");
        var file = Path.GetFileName(fixture.ConfigurationFile!);
        var selected = File.ReadAllBytes(fixture.ConfigurationFile!);
        ConfigurationRevisionBatch candidate;
        using (var source = new SelectedProjectSource(host.Instance, host.Database, host.Lease, host.Resources,
            binding, 1, "maas", () => { }, draft: owner))
        using (var mounts = new ProjectConfigMounts(host.Instance, host.Database, host.Lease, host.Resources, source))
        {
            Assert.True(mounts.TryRead(file, out var first, default));
            Assert.Equal(selected, first!.Content.ToArray());
            var mount = Assert.Single(mounts.Mounts);
            candidate = new([new(address, 0, 1, mount.Saved.Contract, mount.Saved.Tree, ConfigurationUpdateKind.Manual)]);
            Assert.Empty(fixture.Revisions.ReadCurrentRevisions(binding));
        }
        File.WriteAllText(fixture.ConfigurationFile!, "{}");
        using var reopened = new SelectedProjectSource(host.Instance, host.Database, host.Lease, host.Resources,
            binding, 1, "maas", () => { }, draft: owner);
        using var retained = new ProjectConfigMounts(host.Instance, host.Database, host.Lease, host.Resources, reopened, staged: candidate);
        Assert.True(retained.TryRead(file, out var previous, default));
        Assert.Equal(selected, previous!.Content.ToArray());
        Assert.Empty(fixture.Revisions.ReadCurrentRevisions(binding));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishesDraftSecrets(bool cancel)
    {
        using var fixture = new ProjectSourceFixture(configuration: true, file: true);
        var host = fixture.Catalog;
        using var drafts = new BindingDrafts(host.Instance, host.Database, new());
        var definitions = new AutomationDefinitionStore(host.Database);
        var catalog = definitions.Read(); var script = Assert.Single(catalog.Scripts);
        var user = new AutomationUser(Guid.NewGuid(), 0, "Draft account", 2);
        var binding = new AutomationBinding(Guid.NewGuid(), 0, user.Id, script.Id, false, 0);
        var owner = new DraftBindingIdentity(user.Id, script.Id, script.Revision);
        var candidate = Guid.NewGuid(); var parent = Guid.NewGuid();
        var key = new SecretFieldKey("maa-framework", binding.Id, "password");
        var fields = new SecretFieldStore(host.Instance, host.Database);
        var saved = fixture.Revisions.Read(fixture.Address)!;
        var address = new ConfigurationAddress(binding.Id, "account");
        var batch = new ConfigurationRevisionBatch([new(address, 0, 1, saved.Contract, saved.Tree, ConfigurationUpdateKind.Manual)]);
        using var secret = new SecretValue("fixture-draft-value".AsSpan());
        using var changed = new SecretValue("different-fixture-value".AsSpan());
        ProjectProtectedEdit[] Edits(SecretValue value) => [new(new(key.Field, 0, SecretDisposition.Set), value)];
        var proof = DraftProjectSecrets.Proofs(host.Instance, host.Database, candidate, "maa-framework", binding.Id, Edits(secret));
        var encrypted = DraftProjectSecrets.Prepare(host.Instance, "maa-framework", binding.Id, [key.Field], Edits(secret), null, proof);
        var protectedFile = Assert.Single(encrypted.Payloads);
        var file = Path.Combine(host.Instance.RuntimeRoot, "secrets", "payloads", $"{protectedFile.Id:D}-{protectedFile.Payload.Sha256}.bin");
        Assert.Throws<OperationConflictException>(() => encrypted.RequireProofs(
            DraftProjectSecrets.Proofs(host.Instance, host.Database, candidate, "maa-framework", binding.Id, Edits(changed))));
        drafts.StageManaged(candidate, "editor", new string('a', 64), owner, batch, encrypted);
        Assert.Contains(new ResourceClaim("secrets:fields", ResourceClaimMode.Mutation), drafts.Claims([candidate], "editor"));
        Assert.Throws<ConfigurationException>(() => drafts.ReadProtections(candidate, "other", binding.Id, owner));
        Assert.False(File.Exists(file));
        Assert.False((await fields.GetStateAsync(key, default)).IsConfigured);
        if (cancel)
        {
            drafts.Discard(candidate, "editor");
            Assert.Throws<ConfigurationException>(() => drafts.ReadManaged(candidate, "editor", binding.Id, owner));
            Assert.Empty(fixture.Revisions.ReadCurrentRevisions(binding.Id)); Assert.False(File.Exists(file));
            return;
        }
        using (var failed = new DraftSecretFiles(host.Instance, host.Database, [encrypted]))
        {
            Assert.True(File.Exists(file));
            Assert.Throws<IOException>(() => host.Database.Write<int>((connection, transaction) =>
            {
                encrypted.Publish(connection, transaction, parent, DateTimeOffset.UtcNow);
                throw new IOException("fixture rollback");
            }));
        }
        Assert.False(File.Exists(file)); Assert.False((await fields.GetStateAsync(key, default)).IsConfigured);
        var changes = new AutomationChanges(catalog.Revision, [], [], [user], [binding], [], [], []);
        var operation = AutomationDefinitionStore.DraftIdentity(parent, "editor", changes, [candidate]);
        var part = Path.Combine(host.Instance.RuntimeRoot, "secrets", "binding-drafts", $"{protectedFile.Id:D}-{protectedFile.Payload.Sha256}.part");
        var original = new IOException("fixture transaction failed");
        FileStream? occupied = null;
        try
        {
            Assert.Same(original, Assert.Throws<IOException>(() => drafts.Commit(operation, changes, [candidate], DateTimeOffset.UtcNow, _ =>
            {
                occupied = new(part, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                throw original;
            })));
            Assert.Single(CleanupFailures.Read(original));
            Assert.False(File.Exists(file));
            Assert.False((await fields.GetStateAsync(key, default)).IsConfigured);
        }
        finally { occupied?.Dispose(); }
        DraftSecretFiles.Recover(host.Instance, host.Database);
        Assert.True(definitions.CommitDrafts(operation, changes, DateTimeOffset.UtcNow, [candidate], drafts).IsNew);
        Assert.NotNull(fixture.Revisions.Read(address)); Assert.True(File.Exists(file));
        var state = Assert.Single(ProjectInputStates.Read(host.Instance, host.Database, new(), "maa-framework", binding.Id));
        Assert.True(state.IsReadable); Assert.Equal(1, state.Revision);
        using var material = await fields.ReadAsync(key, default);
        material.Use(value => Assert.True(value.SequenceEqual("fixture-draft-value".AsSpan())));
    }

    [Fact]
    public void RejectsStaleProjectDraft()
    {
        using var fixture = new ProjectSourceFixture(configuration: true, file: true);
        var host = fixture.Catalog;
        using var drafts = new BindingDrafts(host.Instance, host.Database, new());
        var definitions = new AutomationDefinitionStore(host.Database);
        var catalog = definitions.Read();
        var script = Assert.Single(catalog.Scripts);
        var root = Assert.Single(catalog.Roots);
        var user = new AutomationUser(Guid.NewGuid(), 0, "Draft account", 2);
        var binding = new AutomationBinding(Guid.NewGuid(), 0, user.Id, script.Id, false, 0);
        var owner = new DraftBindingIdentity(user.Id, script.Id, script.Revision);
        var candidate = Guid.NewGuid();
        var saved = fixture.Revisions.Read(fixture.Address)!;
        var digest = new string('a', 64);
        ExtensionInstalledMetadata Previous(string id) => new(id, 1, true,
            new(id, "0.1.0", ExtensionPackageKind.ManagedCode, digest, digest), Guid.NewGuid(), new("fixture", 1, digest));
        var project = new DraftProjectContract(root.Id, root.Revision, Previous(script.ExtensionId!), Previous("maa-framework"));
        drafts.StageManaged(candidate, "editor", digest, owner,
            new([new(ProjectChoiceStore.Address(binding.Id), 0, 1, saved.Contract, saved.Tree, ConfigurationUpdateKind.Manual)]), project: project);
        Assert.Contains(new ResourceClaim("extension:maa-framework", ResourceClaimMode.Reference), drafts.Claims([candidate], "editor"));
        var changes = new AutomationChanges(catalog.Revision, [], [], [user], [binding], [], [], []);
        var operation = AutomationDefinitionStore.DraftIdentity(Guid.NewGuid(), "editor", changes, [candidate]);
        Assert.Equal("project_selection_contract_changed", Assert.Throws<ConfigurationException>(() =>
            definitions.CommitDrafts(operation, changes, DateTimeOffset.UtcNow, [candidate], drafts)).Message);
        Assert.Null(definitions.Lookup(operation, changes, [candidate]));
        Assert.Equal(catalog.Revision, definitions.Read().Revision);
        Assert.Empty(fixture.Revisions.ReadCurrentRevisions(binding.Id));
        Assert.Single(drafts.ReadManaged(candidate, "editor", binding.Id, owner).Changes);
    }

    [Fact]
    public void ReclaimsExpiredDrafts()
    {
        using var fixture = new ProjectSourceFixture(configuration: true, file: true);
        var host = fixture.Catalog;
        var clock = new DraftClock();
        using var drafts = new BindingDrafts(host.Instance, host.Database, new(), clock);
        var script = Assert.Single(new AutomationDefinitionStore(host.Database).Read().Scripts);
        var owner = new DraftBindingIdentity(Guid.NewGuid(), script.Id, script.Revision);
        var binding = Guid.NewGuid();
        var digest = new string('a', 64);
        var saved = fixture.Revisions.Read(fixture.Address)!;
        var batch = new ConfigurationRevisionBatch([new(new(binding, "account"), 0, 1, saved.Contract, saved.Tree, ConfigurationUpdateKind.Manual)]);
        Guid Stage()
        {
            var id = Guid.NewGuid();
            drafts.StageManaged(id, "editor", digest, owner, batch);
            return id;
        }
        var abandoned = Stage();
        for (var index = 1; index < 63; index++) _ = Stage();
        clock.Advance(TimeSpan.FromMinutes(20));
        var retained = Stage();
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(drafts.IsStaged(abandoned, "editor", Guid.Empty, digest));
        Assert.True(drafts.IsStaged(retained, "editor", Guid.Empty, digest));
        Assert.Single(drafts.ReadManaged(Stage(), "editor", binding, owner).Changes);
        Assert.Empty(fixture.Revisions.ReadCurrentRevisions(binding));
    }

    private sealed class DraftClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }

    [Theory(DisplayName = "Script Source Authority Reads Its Own Root Without An Account And Cannot Produce A Binding Execution Snapshot")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsIndependentScript(bool removeBindings)
    {
        using var fixture = new ProjectSourceFixture();
        fixture.Write("interface.json", "{\"interface_version\":2}"u8.ToArray());
        var owner = fixture.Catalog;
        var store = new AutomationDefinitionStore(owner.Database);
        var catalog = store.Read();
        if (removeBindings)
        {
            var removed = catalog.Bindings.Select(item => new RemoveAutomationDefinition(AutomationDefinitionKind.Binding, item.Id, item.Revision))
                .Concat(catalog.Users.Select(item => new RemoveAutomationDefinition(AutomationDefinitionKind.User, item.Id, item.Revision))).ToImmutableArray();
            var change = new AutomationChanges(catalog.Revision, [], [], [], [], [], [], removed);
            store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
            catalog = store.Read(); Assert.Empty(catalog.Bindings); Assert.Empty(catalog.Users);
        }
        var script = Assert.Single(catalog.Scripts);
        using var source = SelectedProjectSource.ForScript(owner.Instance, owner.Database, owner.Lease, owner.Resources,
            script.Id, script.Revision, "maas", () => { });
        Assert.Equal(script.Id, source.Script.Id); Assert.Equal(script.Revision, source.Script.Revision);
        Assert.Equal("{\"interface_version\":2}"u8.ToArray(), source.Read("interface.json", default)!.Content.ToArray());
        Assert.Equal("project_binding_authority_required", Assert.Throws<IOException>(() => source.Freeze(default)).Message);
        source.RequireCurrent();
        var changes = new AutomationChanges(catalog.Revision, [], [script with { Name = "Changed" }], [], [], [], [], []);
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        Assert.Throws<IOException>(source.RequireCurrent);
    }

    [Theory(DisplayName = "Complete Native Resources Use The Existing Tree Archive Budget And Do Not Consume The Compiler Source File Budget")]
    [InlineData(false)]
    [InlineData(true)]
    public void ArchivesCompleteResources(bool changed)
    {
        using var fixture = new ProjectSourceFixture();
        var directory = Path.Combine(fixture.Path, "resource"); Directory.CreateDirectory(directory);
        var count = changed ? 140 : 2100;
        for (var index = 0; index < count; index++) File.WriteAllText(Path.Combine(directory, $"pipeline-{index}.json"), "{\"pipeline\":" + index + "}");
        using var source = fixture.Open();
        if (!changed)
        {
            var tree = new FileTreeScanner(SnapshotBudget.Default).CaptureArchive(directory, default);
            var claims = PhysicalResourceAdmission.ReadTree(directory, tree); Assert.True(claims.Length > 4096);
            var existing = PhysicalResourceAdmission.Read(fixture.Path, ResourceClaimMode.Reference, includeDescendants: false)
                .Select(item => item.ResourceKey).ToHashSet(StringComparer.Ordinal);
            var conflict = claims.Skip(4096).First(item => !existing.Contains(item.ResourceKey));
            var probe = claims.Take(4096).First(item => !existing.Contains(item.ResourceKey));
            using (var blocked = fixture.Catalog.Resources.TryAcquire(Guid.NewGuid(), [conflict with { Mode = ResourceClaimMode.Mutation }]).Lease!)
            {
                Assert.NotNull(blocked);
                Assert.Throws<IOException>(() => source.CaptureResource("resource", default));
                using var released = fixture.Catalog.Resources.TryAcquire(Guid.NewGuid(), [probe with { Mode = ResourceClaimMode.Mutation }]).Lease;
                Assert.NotNull(released);
            }
        }
        var resource = source.CaptureResource("resource", default);
        Assert.Equal(count, resource.Archive.Snapshot.Entries.Count(item => item.Kind == NexusPipeline.Core.Configuration.FileObjectKind.File));
        Assert.Equal(count, resource.Archive.Contents.Count);
        Assert.Throws<IOException>(() => File.Delete(Path.Combine(directory, "pipeline-0.json")));
        Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(directory, "pipeline-0.json"), "changed"));
        var captured = source.Freeze(default); Assert.Empty(captured.Files); Assert.Single(captured.ResourceTrees);
        foreach (var content in resource.Archive.Contents) new ImmutableBlobStore(fixture.Catalog.Instance, new()).Verify(content);
        if (changed)
        { File.WriteAllText(Path.Combine(directory, "late.json"), "{}"); Assert.Throws<IOException>(source.RequireCurrent); }
        else source.RequireCurrent();
    }

    [Fact(DisplayName = "A Selected Binding Captures Exact Physical Source Bytes Above The Ordinary Json Limit Without Exposing Mutable Owned Buffers")]
    public void CapturesSelectedSources()
    {
        using var fixture = new ProjectSourceFixture();
        var bytes = new byte[3 * 1024 * 1024]; Array.Fill(bytes, (byte)'x'); fixture.Write("large.json", bytes);
        using var source = fixture.Open();
        var read = source.Read("large.json", default)!; Assert.Equal(bytes, read.Content.ToArray());
        System.Runtime.InteropServices.MemoryMarshal.AsMemory(read.Content).Span.Fill(0);
        Assert.Equal(bytes, source.Read("large.json", default)!.Content.ToArray());
        Assert.Null(source.Read("missing.json", default));
        var captured = source.Freeze(default); var file = Assert.Single(captured.Files);
        Assert.Equal(fixture.Binding, captured.BindingId); Assert.Equal("large.json", file.Path);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Content.Sha256);
        using var stored = new ImmutableBlobStore(fixture.Catalog.Instance, new()).OpenVerified(file.Content);
        Assert.Equal(bytes.LongLength, stored.Length); Assert.Equal(file.Content.Sha256, Convert.ToHexStringLower(SHA256.HashData(stored)));
        Assert.Throws<InvalidOperationException>(() => source.Read("large.json", default)); source.RequireCurrent();
    }

    [Theory(DisplayName = "Selected Source Directory And Negative Reads Detect New Files Directories Or Replaced Unread Objects")]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("replacement")]
    [InlineData("missing-parent")]
    public void RejectsChangedListing(string change)
    {
        using var fixture = new ProjectSourceFixture(); fixture.Write("unread.json", "{}"u8.ToArray());
        using var source = fixture.Open();
        Assert.Null(source.Read(change == "missing-parent" ? "new/file.json" : "missing.json", default));
        _ = source.Freeze(default);
        source.RequireCurrent();
        if (change == "directory" || change == "missing-parent") Directory.CreateDirectory(Path.Combine(fixture.Path, "new"));
        else if (change == "replacement")
        {
            var file = Path.Combine(fixture.Path, "unread.json");
            var parked = Path.Combine(Path.GetDirectoryName(fixture.Path)!, $"{Guid.NewGuid():N}.json");
            var entries = Directory.GetFileSystemEntries(fixture.Path).Order(StringComparer.Ordinal).ToArray();
            var before = PhysicalObjectIdentity.Read(file);
            File.Move(file, parked);
            fixture.Write("unread.json", "{}"u8.ToArray());
            Assert.Equal(entries, Directory.GetFileSystemEntries(fixture.Path).Order(StringComparer.Ordinal));
            Assert.Equal(before, PhysicalObjectIdentity.Read(parked));
            Assert.NotEqual(before, PhysicalObjectIdentity.Read(file));
            Assert.Equal("{}", File.ReadAllText(file));
            Assert.Equal("{}", File.ReadAllText(parked));
        }
        else fixture.Write("missing.json", "{}"u8.ToArray());
        Assert.Equal("project_source_changed", Assert.Throws<IOException>(source.RequireCurrent).Message);
    }

    [Fact(DisplayName = "Selected Source File And Parent Handles Plus Physical Reference Claims Remain Held Through Capture")]
    public void HoldsReadFiles()
    {
        using var fixture = new ProjectSourceFixture(); fixture.Write("interface.json", "{}"u8.ToArray());
        using var source = fixture.Open(); Assert.NotNull(source.Read("interface.json", default));
        Assert.Null(source.Read("missing.json", default));
        Assert.Null(fixture.Catalog.Resources.TryAcquire(Guid.NewGuid(), PhysicalResourceAdmission.Read(Path.Combine(fixture.Path, "interface.json"))).Lease);
        Assert.Null(fixture.Catalog.Resources.TryAcquire(Guid.NewGuid(), PhysicalResourceAdmission.Read(Path.Combine(fixture.Path, "missing.json"))).Lease);
        Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(fixture.Path, "interface.json"), "changed"));
        Assert.Throws<IOException>(() => File.Delete(Path.Combine(fixture.Path, "interface.json")));
        Assert.Throws<IOException>(() => Directory.Move(fixture.Path, fixture.Path + "-moved"));
        _ = source.Freeze(default); source.RequireCurrent();
        source.Dispose(); fixture.Write("interface.json", "changed"u8.ToArray());
    }

    [Theory(DisplayName = "Source Reads Reject Absolute Traversal Stream Backslash And Case Alias Paths")]
    [InlineData("../outside.json")]
    [InlineData("D:/outside.json")]
    [InlineData("interface.json:stream")]
    [InlineData("config\\file.json")]
    [InlineData("INTERFACE.JSON")]
    [InlineData("")]
    public void RejectsForeignPaths(string path)
    {
        using var fixture = new ProjectSourceFixture(); fixture.Write("interface.json", "{}"u8.ToArray());
        using var source = fixture.Open(); Assert.Throws<IOException>(() => source.Read(path, default));
    }

    [Fact(DisplayName = "Changed Binding Or Revoked Caller Invalidates A Captured Source Instead Of Reusing Old Authority")]
    public void RejectsChangedBinding()
    {
        using var fixture = new ProjectSourceFixture(); var revoked = false;
        using var source = fixture.Open(() => { if (revoked) throw new UnauthorizedAccessException(); });
        _ = source.List(""); revoked = true;
        Assert.Throws<UnauthorizedAccessException>(source.RequireCurrent); revoked = false;
        var store = new AutomationDefinitionStore(fixture.Catalog.Database); var catalog = store.Read();
        var binding = catalog.Bindings.Single(item => item.Id == fixture.Binding);
        var changes = new AutomationChanges(catalog.Revision, [], [], [], [binding with { Priority = binding.Priority + 1 }], [], [], []);
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        Assert.Throws<IOException>(source.RequireCurrent);
    }

    [Theory(DisplayName = "A Source Enforces Its Own File Count And Per File Budget Before Freezing Content")]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundsSourceInputs(bool large)
    {
        using var fixture = new ProjectSourceFixture();
        if (large)
        { using var file = File.Create(Path.Combine(fixture.Path, "large.json")); file.SetLength(8 * 1024 * 1024 + 1); }
        else for (var index = 0; index < 129; index++) fixture.Write($"file-{index}.json", "{}"u8.ToArray());
        using var source = fixture.Open();
        if (large) Assert.Throws<IOException>(() => source.Read("large.json", default));
        else
        {
            for (var index = 0; index < 128; index++) Assert.NotNull(source.Read($"file-{index}.json", default));
            Assert.Throws<IOException>(() => source.Read("file-128.json", default));
        }
    }

}

internal sealed class ProjectSourceFixture : IDisposable
{
    internal ExtensionCatalogFixture Catalog { get; }
    private readonly bool _ownsCatalog;
    internal string Path { get; }
    internal Guid Binding { get; }
    internal string? ConfigurationFile { get; }
    internal bool HasTaskProjection { get; }
    internal bool HasTaskRetry { get; }
    private readonly string _extensionId;
    internal ConfigurationRevisionStore Revisions => new(Catalog.Instance, Catalog.Database, new());
    internal ConfigurationAddress Address => new(Binding, "account");

    internal ProjectSourceFixture(bool configuration = false, bool file = false, bool enabled = false, bool taskProjection = false, bool taskRetry = false,
        ExtensionCatalogFixture? catalog = null, string configurationName = "local.json", string extensionId = "maas")
    {
        _extensionId = extensionId;
        Catalog = catalog ?? new(); _ownsCatalog = catalog is null;
        HasTaskProjection = taskProjection;
        HasTaskRetry = taskRetry;
        var changes = AutomationDefinitionSeed.Create(System.IO.Path.GetDirectoryName(Catalog.Instance.RuntimeRoot)!);
        changes = changes with { Scripts = [changes.Scripts[0] with { Kind = AutomationKind.Specialized, ExtensionId = extensionId }] };
        if (taskRetry) changes = changes with { Scripts = [changes.Scripts[0] with
            { Judgement = new(AutomationJudgementLanguage.Keywords, [], [], null) }] };
        if (enabled) changes = changes with { Bindings = changes.Bindings.Select((item, index) => item with { IsEnabled = index == 0 }).ToImmutableArray() };
        if (configuration)
        {
            var script = changes.Scripts[0]; var alias = file ? configurationName : "config";
            changes = changes with { Scripts = [script with { ConfigurationPaths = [new(script.RootId, alias)], AdapterResources =
                [new("account", 0, file ? "" : configurationName, 1, ConfigurationSyntax.Json,
                    new(taskProjection ? [new(new([]), true)] : [], [new(new([]), true)], []))] }] };
        }
        var store = new AutomationDefinitionStore(Catalog.Database);
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        Binding = changes.Bindings[0].Id; Path = changes.Roots[0].Path;
        if (configuration)
        {
            var directory = System.IO.Path.Combine(Path, "config"); Directory.CreateDirectory(directory);
            ConfigurationFile = System.IO.Path.Combine(file ? Path : directory, configurationName);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigurationFile)!);
            File.WriteAllText(ConfigurationFile, taskProjection
                ? "{\"account\":\"saved-account\",\"TaskItems\":[],\"taskA\":true,\"taskB\":true,\"unlisted\":true,\"counter\":17}"
                : "{\"account\":\"saved-account\",\"TaskItems\":[]}");
            var script = store.Read().Scripts[0];
            var tree = new FileTreeArchive(new FileTreeScanner(SnapshotBudget.Default), new(Catalog.Instance, new()))
                .Capture(file ? ConfigurationFile : directory, default);
            Save(new(Address, 0, 1, AdapterConfigurationPolicy.Contract(script, script.AdapterResources[0]), tree.Snapshot, ConfigurationUpdateKind.Manual));
        }
    }

    internal void Write(string path, byte[] bytes) => File.WriteAllBytes(System.IO.Path.Combine(Path, path), bytes);
    internal SelectedProjectSource Open(Action? check = null) => new(Catalog.Instance, Catalog.Database, Catalog.Lease, Catalog.Resources,
        Binding, 1, _extensionId, check ?? (() => { }));
    internal void Save(ConfigurationRevisionChange change)
    {
        var batch = new ConfigurationRevisionBatch([change]); var store = new ConfigurationBatchStore(Catalog.Instance, Catalog.Database, new());
        using var prepared = store.Prepare(batch);
        store.Commit(ConfigurationBatchStore.Identity(Guid.NewGuid(), "fixture", batch), prepared, DateTimeOffset.UtcNow);
    }
    public void Dispose() { if (_ownsCatalog) Catalog.Dispose(); }
}
