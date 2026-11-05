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
            await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Session().ApplyAsync(fixture.Prepared, default));
            Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "activated.json")));
            Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "resource.bin")));
            Assert.Empty(duplicates);
            Assert.Empty(fixture.Activator.ReadPublishedScopes());
            using var connection = DatabaseConnections.Open(fixture.Package.Catalog.Database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            Assert.Null(ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, "fixture-plugin"));
            Assert.Null(fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan).Commit);
        }
        finally { AppDomain.CurrentDomain.AssemblyLoad -= observer; }
    }
}
