using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

public sealed class ConfigurationOverviewTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "A Generic Configuration Overview Can Import Its Declared Resource For Only The Selected Binding")]
    public void BindsGenericRevisions()
    {
        using var fixture = new PlanBuilderFixture(saveConfigurations: false);
        var catalog = fixture.Definitions.Read();
        var binding = catalog.Bindings.Single(item => item.Id == fixture.BindingA);
        var script = catalog.Scripts.Single(item => item.Id == fixture.ScriptId);
        var overview = new ConfigurationOverviewReader(fixture.Instance, fixture.Database, fixture.Lease, new ContractReader());

        var (beforeSave, missingQueries) = ObserveRevisionReads("generic-missing", () => overview.Read(binding.Id), output);
        var unsaved = Assert.Single(beforeSave.Resources);
        Assert.Equal(0, unsaved.Revision);
        Assert.False(unsaved.IsSaved);
        Assert.Equal(FileObjectKind.Missing, unsaved.Shape);

        var importer = new ConfigurationImportService(fixture.Instance, fixture.Database, new(), fixture.Lease, fixture.Resources);
        importer.Import("configuration-overview", new(Guid.NewGuid(), beforeSave.BindingId,
            beforeSave.BindingRevision, beforeSave.ScriptRevision, [new(unsaved.ResourceId, unsaved.Revision)]), DateTimeOffset.UtcNow);

        var (afterSave, savedQueries) = ObserveRevisionReads("generic-saved", () => overview.Read(binding.Id), output);
        var saved = Assert.Single(afterSave.Resources);
        Assert.Equal(binding.Id, afterSave.BindingId);
        Assert.Equal(binding.Revision, afterSave.BindingRevision);
        Assert.Equal(script.Id, afterSave.ScriptId);
        Assert.Equal(script.Revision, afterSave.ScriptRevision);
        Assert.Equal(unsaved.ResourceId, saved.ResourceId);
        Assert.Equal(1, saved.Revision);
        Assert.True(saved.IsSaved);
        Assert.Equal(FileObjectKind.File, saved.Shape);
        Assert.Equal(1, saved.FileCount);

        var other = Assert.Single(overview.Read(fixture.BindingB).Resources);
        Assert.Equal(unsaved.ResourceId, other.ResourceId);
        Assert.Equal(0, other.Revision);
        Assert.False(other.IsSaved);
        var values = new ValueService(fixture.Instance, fixture.Database, new(), fixture.Lease, fixture.Resources);
        var (resources, valueQueries) = ObserveRevisionReads("values-saved", () => values.Read(binding.Id), output);
        Assert.Equal(1, Assert.Single(resources).Revision);

        var address = new ConfigurationAddress(binding.Id, saved.ResourceId);
        using (var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true, allowPooling: false,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Content))
        using (var transaction = DatabaseConnections.BeginRead(connection))
        {
            var statements = new List<string>();
            SQLitePCL.strdelegate_trace trace = (_, sql) => statements.Add(sql);
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
            try
            {
                _ = ConfigurationRevisionRows.Binding(connection, transaction, address.BindingId);
                Assert.NotNull(ConfigurationRevisionRows.Current(connection, transaction, address,
                    new(new ImmutableBlobStore(fixture.Instance, new()))));
            }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace?)null, null); }
            Assert.Equal(4, statements.Count);
            foreach (var sql in statements)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "EXPLAIN QUERY PLAN " + sql;
                using var planner = command.ExecuteReader();
                while (planner.Read()) output.WriteLine(planner.GetString(3));
            }
        }

        fixture.Database.Write((connection, transaction) =>
        {
            using var command = DatabaseConnections.Command(connection, transaction,
                "DELETE FROM configuration.configuration_revision_assets WHERE binding_id=$0 AND resource_id=$1;",
                address.BindingId.ToString("D"), address.ResourceId);
            Assert.Equal(1, command.ExecuteNonQuery());
            return true;
        });
        Assert.Equal("configuration_reference_closure_invalid", Assert.Throws<IOException>(() => overview.Read(binding.Id)).Message);
        Assert.Equal(3, missingQueries);
        Assert.Equal(4, savedQueries);
        Assert.Equal(4, valueQueries);
    }

    [Fact(DisplayName = "A Specialized Configuration Overview Binds Its Resource And Rejects A Foreign Owner Contract")]
    public void BindsSpecializedContract()
    {
        using var fixture = new PlanBuilderFixture(specialized: true, saveConfigurations: false);
        var definitions = fixture.Definitions;
        var catalog = definitions.Read();
        var binding = catalog.Bindings.Single(item => item.Id == fixture.BindingA);
        var script = catalog.Scripts.Single(item => item.Id == fixture.ScriptId);
        var declaration = new AutomationAdapterResource("native-settings", 0, "", 1,
            ConfigurationSyntax.Json, new([], [], []));
        var declare = new AutomationChanges(catalog.Revision, [], [script with { AdapterResources = [declaration] }],
            [], [], [], [], []);
        definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "configuration-overview", declare), declare, DateTimeOffset.UtcNow);
        catalog = definitions.Read();
        script = catalog.Scripts.Single(item => item.Id == fixture.ScriptId);
        binding = catalog.Bindings.Single(item => item.Id == fixture.BindingA);
        declaration = Assert.Single(script.AdapterResources);
        var path = script.ConfigurationPaths[declaration.DeclarationIndex];
        var root = catalog.Roots.Single(item => item.Id == path.RootId);
        var target = Path.Combine(root.Path, path.RelativePath);
        var captured = new FileTreeArchive(new(SnapshotBudget.Default), new(fixture.Instance, new()))
            .Capture(target, CancellationToken.None).Snapshot;
        var change = new ConfigurationRevisionChange(new(binding.Id, declaration.ResourceId), 0, binding.Revision,
            AdapterConfigurationPolicy.Contract(script, declaration), captured, ConfigurationUpdateKind.Manual);
        var revisions = new ConfigurationRevisionStore(fixture.Instance, fixture.Database, new ContractReader());
        var overview = new ConfigurationOverviewReader(fixture.Instance, fixture.Database, fixture.Lease, new ContractReader());
        var (before, missingQueries) = ObserveRevisionReads("adapter-missing", () => overview.Read(binding.Id), output);
        Assert.False(Assert.Single(before.Resources).IsSaved);
        revisions.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "configuration-overview", change), change, DateTimeOffset.UtcNow);

        var (current, savedQueries) = ObserveRevisionReads("adapter-saved", () => overview.Read(binding.Id), output);
        var resource = Assert.Single(current.Resources);
        Assert.Equal(AutomationKind.Specialized, current.Kind);
        Assert.Equal(script.ExtensionId, current.ExtensionId);
        Assert.Equal(declaration.ResourceId, resource.ResourceId);
        Assert.Equal(declaration.Syntax, resource.Syntax);
        Assert.Equal(1, resource.Revision);
        Assert.True(resource.IsSaved);

        var wrongOwner = script with { ExtensionId = "foreign-extension" };
        var wrongContract = change with { ExpectedRevision = 1,
            Contract = AdapterConfigurationPolicy.Contract(wrongOwner, declaration) };
        revisions.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "configuration-overview", wrongContract),
            wrongContract, DateTimeOffset.UtcNow);
        Assert.Equal("configuration_required",
            Assert.Throws<ConfigurationException>(() => overview.Read(binding.Id)).Message);
        Assert.Equal(3, missingQueries);
        Assert.Equal(4, savedQueries);
    }

    internal static (T Result, long Queries) ObserveRevisionReads<T>(string scenario, Func<T> read, ITestOutputHelper output)
    {
        var previous = Console.Error;
        using var capture = new StringWriter();
        T result;
        try
        {
            Console.SetError(capture);
            using var profile = StartupProfile.Begin("configuration-overview-read-test");
            result = read();
        }
        finally { Console.SetError(previous); }
        using var report = JsonDocument.Parse(capture.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"phase\":\"configuration-overview-read-test\"", StringComparison.Ordinal)));
        var queries = report.RootElement.GetProperty("steps").EnumerateArray()
            .Single(step => step.GetProperty("name").GetString() == ConfigurationRevisionRows.ReadQuery).GetProperty("count").GetInt64();
        const int iterations = 30;
        for (var i = 0; i < 3; i++) _ = read();
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++) _ = read();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var cpu = process.TotalProcessorTime - cpuBefore;
        output.WriteLine($"configuration-overview scenario={scenario} selects={queries} repetitions={iterations} elapsed-ms={elapsed.TotalMilliseconds:F3} process-cpu-ms={cpu.TotalMilliseconds:F3} process-allocated-bytes={allocated}");
        return (result, queries);
    }
}
