using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SdkIdentityTests
{
    [Theory(DisplayName = "Actual Case Changed Sdk Metadata Cannot Create Another Sdk Or Activate An Installed Managed Package")]
    [InlineData("reference-and-local-sdk")]
    [InlineData("reserved-entry")]
    [InlineData("valid")]
    public async Task RejectsDuplicateSdk(string mode)
    {
        var sdk = typeof(IExtension).Assembly;
        var name = sdk.GetName().Name!;
        var duplicates = new ConcurrentQueue<Assembly>();
        AssemblyLoadEventHandler observer = (_, args) =>
        {
            if (!ReferenceEquals(args.LoadedAssembly, sdk) &&
                StringComparer.OrdinalIgnoreCase.Equals(args.LoadedAssembly.GetName().Name, name))
                duplicates.Enqueue(args.LoadedAssembly);
        };
        AppDomain.CurrentDomain.AssemblyLoad += observer;
        try
        {
            if (mode == "valid")
            {
                using var valid = new InstallRuntimeFixture(managedMode: "return");
                try
                {
                    var installed = await valid.Session().ApplyAsync(valid.Prepared, default);
                    Assert.Equal(1, installed.InstalledRevision);
                    Assert.True(installed.IsInstalled);
                    var scope = Assert.Single(valid.Activator.ReadPublishedScopes());
                    Assert.Equal("fixture-plugin", scope.ExtensionId);
                    Assert.Equal(scope.ActivationId, installed.ActivationId);
                    Assert.True(File.Exists(Path.Combine(valid.ActivationDirectory, "activated.json")));
                    Assert.Empty(duplicates);
                    return;
                }
                finally { await valid.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
            }
            using var fixture = new InstallRuntimeFixture(managedMode: "return",
                managedPayloadEdit: directory =>
                {
                    var entry = Path.Combine(directory, "entry.dll");
                    if (mode == "reserved-entry")
                    {
                        File.Copy(sdk.Location, entry, overwrite: true);
                        SdkCaseFixture.RewriteDefinition(entry, name);
                    }
                    else
                    {
                        SdkCaseFixture.RewriteReference(entry, name);
                        var localSdk = Path.Combine(directory, name.ToLowerInvariant() + ".dll");
                        File.Copy(sdk.Location, localSdk, overwrite: false);
                        SdkCaseFixture.RewriteDefinition(localSdk, name);
                    }
                });
            if (mode == "reserved-entry")
            {
                var conflict = await Assert.ThrowsAsync<IOException>(async () => await fixture.Session().ApplyAsync(fixture.Prepared, default));
                Assert.Equal("extension_managed_assembly_conflict", conflict.Message);
            }
            else
            {
                var rejected = await Assert.ThrowsAsync<FileLoadException>(async () => await fixture.Session().ApplyAsync(fixture.Prepared, default));
                Assert.Equal("extension_sdk_assembly_mismatch", Assert.IsType<FileLoadException>(rejected.InnerException).Message);
            }
            Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "activated.json")));
            Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "resource.bin")));
            Assert.Empty(duplicates);
            Assert.Empty(fixture.Activator.ReadPublishedScopes());
            using var connection = DatabaseConnections.Open(fixture.Package.Catalog.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
            using var transaction = connection.BeginTransaction(deferred: true);
            Assert.Null(ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, "fixture-plugin"));
            Assert.Null(fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan).Commit);
        }
        finally { AppDomain.CurrentDomain.AssemblyLoad -= observer; }
    }
}
