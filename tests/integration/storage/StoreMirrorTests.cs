using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Operations;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class StoreMirrorTests
{
    [Fact]
    public async Task CyclesOriginalPackages()
    {
        using var inputs = new SignedStoreInputs();
        await using var mirror = await StoreMirror.StartAsync(); inputs.Populate(mirror);
        await using var fixture = new StoreMirrorRuntime(inputs, mirror);
        var owner = fixture.Owner; var runtime = fixture.Runtime; var service = fixture.Service;
        var settings = fixture.Settings; var roots = fixture.Roots;
        using var original = JsonDocument.Parse(inputs.Catalog);
        var ids = original.RootElement.GetProperty("entries").EnumerateArray()
            .Select(item => item.GetProperty("extensionId").GetString()!).Order().ToArray();
        Assert.Equal(14, ids.Length);
        var parent = Path.GetDirectoryName(owner.Instance.RuntimeRoot)!;
        await runtime.StartAsync(default);
        var catalog = await service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        Assert.Equal(ids, catalog.Entries.Select(item => item.ExtensionId).Order());
        foreach (var entry in catalog.Entries)
        {
            var command = new ExtensionApplyCommand(ExtensionApplyAction.Install, entry.ExtensionId, 0,
                settings.Read().Revision, catalog.Identity, entry);
            var operation = Guid.NewGuid(); var accepted = await service.ApplyAsync(operation, "local", command, default);
            await CompleteAsync(owner, roots, operation, accepted.JournalId, entry.ExtensionId);
            var replay = await service.ApplyAsync(operation, "local", command, default);
            Assert.Equal(accepted.JournalId, replay.JournalId);
        }
        Assert.Equal(ids, runtime.ReadStates().Where(item => item.IsInstalled).Select(item => item.ExtensionId).Order());
        Assert.All(runtime.ReadStates(), item => { Assert.False(item.DesiredEnabled); Assert.Null(item.ActivationId); });
        mirror.Offline = true;
        Assert.Equal(catalog.Identity, service.ReadCached(ExtensionStoreChannel.Stable)!.Identity);
        await Assert.ThrowsAsync<IOException>(() => service.RefreshAsync("local", ExtensionStoreChannel.Stable, default));
        Assert.Equal(catalog.Identity, service.ReadCached(ExtensionStoreChannel.Stable)!.Identity);
        foreach (var state in runtime.ReadStates())
        {
            var operation = Guid.NewGuid();
            var accepted = await service.ApplyAsync(operation, "local", new(ExtensionApplyAction.Remove, state.ExtensionId,
                state.InstalledRevision, settings.Read().Revision, null, null), default);
            await CompleteAsync(owner, roots, operation, accepted.JournalId, state.ExtensionId);
        }
        Assert.All(runtime.ReadStates(), item => Assert.False(item.IsInstalled));
        Assert.Equal(14, mirror.Requests.Count(uri => uri.EndsWith(".zip", StringComparison.Ordinal)));
        inputs.VerifyUnchanged();
        File.WriteAllBytes(Path.Combine(parent, "store-mirror-evidence.json"), JsonSerializer.SerializeToUtf8Bytes(new
            { catalog.Identity, Installed = ids, Removed = ids, Requests = mirror.Requests.ToArray(), OfflineCatalogPreserved = true }));
    }

    private static async Task CompleteAsync(ExtensionCatalogFixture owner, PayloadApplyRoots roots, Guid operation,
        Guid journal, string extensionId)
    {
        var queries = new OperationQueryService(owner.Instance, owner.Lease, owner.Database);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (queries.Read(operation, "local") is { Status: "completed" })
            {
                using var held = owner.Resources.TryAcquire(Guid.NewGuid(), [new("extension:" + extensionId, ResourceClaimMode.Reference)]).Lease;
                if (held is not null) break;
            }
            await Task.Delay(25, deadline.Token);
        }
        var bound = new ExtensionPayloadBinding(owner.Instance, owner.Database, owner.Contracts, roots).Read(journal);
        Assert.Equal(JournalPhase.Committed, bound.Registered.Phase); Assert.NotNull(bound.Commit);
    }
}
