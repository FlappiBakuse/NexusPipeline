using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ManagedInstallationTests
{
    [Fact(DisplayName = "The Actual Managed Loader Shares The Sdk And Keeps Its Owned Resource Until The Original Scope Closes")]
    public async Task SharesLoadedSdk()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "return");
        var result = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        Assert.True(result.IsInstalled); Assert.True(result.IsEnabled);
        using var activated = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.ActivationDirectory, "activated.json")));
        Assert.Equal(fixture.ActivationNonce, activated.RootElement.GetProperty("Nonce").GetString());
        Assert.Equal(typeof(IExtension).Assembly.FullName, activated.RootElement.GetProperty("SdkAssembly").GetString());
        Assert.Equal(typeof(IExtension).Assembly.Location, activated.RootElement.GetProperty("SdkLocation").GetString());
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        var resource = Path.Combine(fixture.ActivationDirectory, "resource.bin");
        Assert.Throws<IOException>(() => { using var denied = File.Open(resource, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        Assert.True(fixture.Activator.IsPublished("fixture-plugin", result.ActivationId!.Value));
        using var root = fixture.Package.Catalog.Resources.TryAcquire(Guid.NewGuid(),
            [new("extension:fixture-plugin", ResourceClaimMode.Mutation)]).Lease!;
        Assert.NotNull(root);
        using var reservation = await fixture.Activator.ReserveForApplyAsync(root, "fixture-plugin", default);
        Assert.False(fixture.Activator.IsPublished("fixture-plugin", result.ActivationId.Value));
        using var cleaned = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
        Assert.Equal(fixture.ActivationNonce, cleaned.RootElement.GetProperty("Nonce").GetString());
        using var readable = File.Open(resource, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(readable.Length > 0);
    }

    [Fact(DisplayName = "An Actually Disabled Managed Installation Commits Without Calling The Managed Entry")]
    public async Task KeepsDisabledInstall()
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: false, managedMode: "return");
        var result = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        Assert.True(result.IsInstalled); Assert.False(result.IsEnabled); Assert.Null(result.ActivationId);
        Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "activated.json")));
        Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "resource.bin")));
        Assert.Equal("not-attempted", fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan).Commit!.ActivationState);
    }

    [Fact(DisplayName = "A Real Managed Callback Failure Closes Its Registered Resource Without Inventing Installation Or Rollback")]
    public async Task CollectsFailedCallbacks()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "throw");
        await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Session().ApplyAsync(fixture.Prepared, default));
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "activated.json")));
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
        using var resource = File.Open(Path.Combine(fixture.ActivationDirectory, "resource.bin"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(resource.Length > 0);
        var bound = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        Assert.Equal(JournalPhase.Committed, bound.Registered.Phase); Assert.Null(bound.Commit);
        Assert.Equal("committed", bound.Restoration[^1].Record.Stage);
        Assert.NotNull(bound.Restoration[0].Record.Failure);
        using var connection = DatabaseConnections.Open(fixture.Package.Catalog.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Null(ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, "fixture-plugin"));
        Assert.False(Directory.Exists(fixture.Target));
    }
}
