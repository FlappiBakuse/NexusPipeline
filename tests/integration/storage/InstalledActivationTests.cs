using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstalledActivationTests
{
    [Theory(DisplayName = "The Actual Installed Loader Can Activate The Original Version After A Newer Head Or Entry Removal")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadsHistoricalPackages(bool removed)
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: false);
        _ = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        Enable(fixture);
        var signed = removed ? fixture.Package.Catalog.Signed(2, edit: document => document["entries"] = new JsonArray()) :
            fixture.Package.Signed(2);
        fixture.Package.Catalog.Store.Accept(Guid.NewGuid(), "local", fixture.Package.Catalog.Verify(signed));
        var raised = fixture.Package.Catalog.NewStore(fixture.Package.Catalog.NewVerifier(
            fixture.Package.Catalog.Signer.ExportSubjectPublicKeyInfo(), stableFloor: 100));
        var before = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        var result = await Service(fixture, raised).ActivateAsync("fixture-plugin", default);
        Assert.Equal(1, result.InstalledRevision);
        Assert.True(fixture.Activator.IsPublished("fixture-plugin", result.ActivationId));
        var after = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        Assert.Equal(before.Commit, after.Commit); Assert.Equal(before.Latest.Export(), after.Latest.Export());
        Assert.Equal("0.5.0", after.Footprint!.Package.PackageVersion);
        await Close(fixture);
    }

    [Fact(DisplayName = "Explicitly Enabling An Installed Managed Package Uses The Actual Loader And Original Scope Cleanup")]
    public async Task EnablesInstalledScopes()
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: false, managedMode: "return");
        _ = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose(); Enable(fixture);
        var before = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        var result = await Service(fixture, fixture.Package.Catalog.Store).ActivateAsync("fixture-plugin", default);
        Assert.True(fixture.Activator.IsPublished("fixture-plugin", result.ActivationId));
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "activated.json")));
        Assert.Throws<IOException>(() => { using var locked = File.Open(Path.Combine(fixture.ActivationDirectory, "resource.bin"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        await Close(fixture);
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
        Assert.Equal(before.Latest.Export(), fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan).Latest.Export());
    }

    [Fact(DisplayName = "A Current Revoked Key Cannot Activate The Previously Installed Signed Package")]
    public async Task RejectsRevokedKeys()
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: false);
        _ = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose(); Enable(fixture);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var revoked = fixture.Package.Catalog.NewStore(fixture.Package.Catalog.NewVerifier(other.ExportSubjectPublicKeyInfo()));
        await Assert.ThrowsAnyAsync<Exception>(async () => await Service(fixture, revoked).ActivateAsync("fixture-plugin", default));
        var before = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        Assert.Equal("not-attempted", before.Commit!.ActivationState);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Service(fixture,
            fixture.Package.Catalog.Store).ActivateAsync("fixture-plugin", new CancellationToken(canceled: true)));
    }

    [Fact(DisplayName = "A Parallel Formal Activator Cannot Replace The Instance Owned Inventory")]
    public void RejectsParallelActivators()
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: false);
        var failure = Assert.Throws<IOException>(() => new ExtensionActivator(fixture.Package.Catalog.Contracts,
            fixture.Package.Catalog.Lease, fixture.Package.Catalog.Resources));
        Assert.Equal("extension_scope_inventory_already_bound", failure.Message);
    }

    [Fact(DisplayName = "Startup Activation Reuses Protected Metadata And Still Rejects New Journal Entries")]
    public async Task ProtectsStartupEvidence()
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: false);
        _ = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose(); Enable(fixture);
        var context = fixture.Package.Catalog;
        ExtensionProjectionPublication.PublishIfDirty(context.Instance, context.Database, context.Lease,
            context.Contracts, fixture.Roots, context.Resources, context.Index);
        var installed = new ExtensionInstalledEvidence(context.Instance, context.Database, context.Contracts, fixture.Roots)
            .ReadCurrent("fixture-plugin")!;
        using var root = context.Resources.TryAcquire(Guid.NewGuid(), InstalledActivationOrigin.Claims(context.Instance, installed)).Lease!;
        using var inventory = await fixture.Activator.ReserveForApplyAsync(root, "fixture-plugin", default);
        var origin = InstalledActivationOrigin.Prepare(context.Instance, context.Database, context.Lease, context.Contracts,
            fixture.Roots, context.Store, context.Resources, context.Index, root, inventory, "fixture-plugin");
        var directory = Path.Combine(context.Instance.RuntimeRoot, "journals", installed.Metadata.JournalId.ToString("D"));
        var revision = Directory.GetFiles(directory).First();
        var bytes = File.ReadAllBytes(revision);
        try
        {
            var bound = origin.RequireCurrent();
            void VerifyCurrent()
            {
                var current = origin.RequireCurrent();
                Assert.Equal(bound.Registered.JournalId, current.Registered.JournalId);
                Assert.Equal(bound.PlanReference, current.PlanReference);
                Assert.Equal(bound.Identity, current.Identity);
                Assert.Equal(bound.Latest.Revision, current.Latest.Revision);
                Assert.Equal(bound.Latest.Phase, current.Latest.Phase);
            }
            VerifyCurrent();
            await RechecksWriterState(context.Database, bound.Registered.JournalId, origin);
            var catalog = bound.Source.Package!.Catalog;
            void SetReady(int ready)
            {
                using var connection = DatabaseConnections.Open(context.Database.DatabasePath, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE main.recovery_index_state SET ready=$ready WHERE singleton=1;";
                command.Parameters.AddWithValue("$ready", ready);
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            SetReady(0);
            try
            {
                Assert.Throws<IOException>(() => origin.RequireCurrent());
                Assert.Throws<IOException>(() => origin.RequireCurrent());
            }
            finally { SetReady(1); }
            VerifyCurrent();
            foreach (var reference in new[] { catalog.Pointer, catalog.Catalog, catalog.Signature })
                Assert.Throws<IOException>(() => { using var writer = File.Open(Path.Combine(context.Instance.RuntimeRoot,
                    reference.RelativePath), FileMode.Open, FileAccess.Write, FileShare.Read); });
            context.Database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "DELETE FROM extensions.extension_catalog_heads;";
                return command.ExecuteNonQuery();
            });
            try { Assert.Throws<IOException>(() => origin.RequireCurrent()); }
            finally
            {
                context.Database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "INSERT INTO extensions.extension_catalog_heads(format,channel,sequence,catalog_sha) " +
                        "SELECT format,channel,sequence,catalog_sha FROM extensions.extension_catalog_acceptances;";
                    return command.ExecuteNonQuery();
                });
            }
            VerifyCurrent();
            Assert.Throws<IOException>(() => { using var writer = File.Open(revision, FileMode.Open, FileAccess.Write, FileShare.Read); });
            void ChangePhase(string phase)
            {
                using var connection = DatabaseConnections.Open(context.Database.DatabasePath, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE main.operation_journals SET phase=$phase WHERE journal_id=$journal;";
                command.Parameters.AddWithValue("$phase", phase);
                command.Parameters.AddWithValue("$journal", bound.Registered.JournalId.ToString("D"));
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            ChangePhase("applied");
            try
            {
                Assert.Throws<IOException>(() => origin.RequireCurrent());
                Assert.Throws<IOException>(() => origin.RequireCurrent());
            }
            finally { ChangePhase("committed"); }
            VerifyCurrent();
            var unexpected = Path.Combine(directory, "unexpected.bin");
            File.WriteAllBytes(unexpected, [1]);
            try { Assert.Throws<IOException>(() => origin.RequireCurrent()); }
            finally { File.Delete(unexpected); }
            VerifyCurrent();
        }
        finally { origin.Dispose(); }
        using (var writer = File.Open(revision, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
        Assert.Equal(bytes, File.ReadAllBytes(revision));
        Assert.Throws<ObjectDisposedException>(() => origin.RequireCurrent());
    }

    private static InstalledActivationService Service(InstallRuntimeFixture fixture, ExtensionCatalogStore catalog) =>
        new(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Database, fixture.Package.Catalog.Lease,
            fixture.Package.Catalog.Contracts, fixture.Roots, fixture.Package.Catalog.Resources, fixture.Package.Catalog.Index,
            catalog, fixture.Activator);

    private static async Task RechecksWriterState(StateDatabase database, Guid journal, InstalledActivationOrigin origin)
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "writer-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var job = new OwnedWorkerJob();
        var process = job.Start(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!,
            ["process-output", "wait", Path.Combine(directory, "ready")], directory, Guid.NewGuid(), Guid.NewGuid(), _ => { });
        var writer = new JournalWriter(Guid.NewGuid(), ProcessIdentityReader.Read(process.ProcessId));
        try
        {
            database.Write((connection, transaction) =>
            {
                JournalWriterStore.Insert(connection, transaction, journal, writer); return true;
            });
            Assert.Equal("extension_original_writer_stop_unconfirmed", Assert.Throws<IOException>(() => origin.RequireCurrent()).Message);
            Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
            _ = origin.RequireCurrent();
        }
        finally
        {
            database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "DELETE FROM main.journal_writers WHERE journal_id=$journal AND writer_id=$writer;";
                command.Parameters.AddWithValue("$journal", journal.ToString("D"));
                command.Parameters.AddWithValue("$writer", writer.WriterId.ToString("D"));
                return command.ExecuteNonQuery();
            });
        }
        _ = origin.RequireCurrent();
    }

    private static void Enable(InstallRuntimeFixture fixture)
    {
        var operation = Guid.NewGuid();
        using var root = fixture.Package.Catalog.Resources.TryAcquire(operation,
            [new("settings:host", ResourceClaimMode.Mutation)]).Lease!;
        Assert.NotNull(root);
        var store = new HostSettingsStore(fixture.Package.Catalog.Database); var current = store.Read();
        var change = new HostSettingsChange(current.Revision, current.Values with { Extensions = [new("fixture-plugin", true)] });
        _ = store.Commit(HostSettingsStore.Identity(operation, "local", change), change, DateTimeOffset.UtcNow);
    }

    private static async Task Close(InstallRuntimeFixture fixture)
    {
        using var root = fixture.Package.Catalog.Resources.TryAcquire(Guid.NewGuid(),
            [new("extension:fixture-plugin", ResourceClaimMode.Mutation)]).Lease!;
        Assert.NotNull(root);
        using var stopped = await fixture.Activator.ReserveForApplyAsync(root, "fixture-plugin", default);
    }
}
