using System.Text.Json;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class BaahRepairTests
{
    [Fact(DisplayName = "A Packaged BAAH Blocking File Log Diagnostic Allows Its Exact Repair And Rejects A Changed Proposal")]
    public async Task RepairsBlockedLogging()
    {
        await using var fixture = new BaahFixture(automatic: false, fileLogging: false);
        await fixture.InstallAsync();
        var source = fixture.Source; const string subject = "baah-repair";
        var binding = source.Definitions.Read().Bindings.Single(item => item.Id == source.OtherBinding);
        var installed = new ExtensionInstalledEvidence(source.Instance, source.Database, new(), fixture.Installation.Roots).ReadCurrent("baah")!;
        var selection = new AdapterSelection(Guid.NewGuid(), binding.Id, binding.Revision, "baah", installed.Metadata.Revision,
            [new("main", 1, ""), new("software-config", 1, "")]);
        var proposal = await fixture.Adapters.ProposeConfigurationAsync(subject, selection, default);
        Assert.Contains(proposal.Diagnostics, diagnostic => diagnostic.Severity == AdapterDiagnosticSeverity.Blocking);
        Assert.Single(proposal.Patches);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Adapters.PreviewRepairAsync(subject,
            selection, 0, proposal.ProposalDigest, default).AsTask());
        var settings = source.Settings.Read(); var change = new HostSettingsChange(settings.Revision, settings.Values with { AllowConfigRepair = true });
        source.Settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), subject, change), change, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<IOException>(() => fixture.Adapters.PreviewRepairAsync(subject, selection, 0, new string('a', 64), default).AsTask());
        var preview = await fixture.Adapters.PreviewRepairAsync(subject, selection, 0, proposal.ProposalDigest, default);
        Assert.True(preview.HasChanges);
        var applied = await fixture.Adapters.ApplyRepairAsync(subject, selection, 0, proposal.ProposalDigest, DateTimeOffset.UtcNow, default);
        Assert.True(applied.IsNew); Assert.Equal(2, applied.Saved.Revision);
        var tree = applied.Saved.Tree;
        var file = Assert.Single(tree.Entries, entry => entry.Kind == FileObjectKind.File);
        using var bytes = new ImmutableBlobStore(source.Instance, new()).OpenVerified(new($"blobs/content/{file.Sha256}.bin", file.SizeBytes, file.Sha256!));
        using var document = JsonDocument.Parse(bytes);
        Assert.True(document.RootElement.GetProperty("SAVE_LOG_TO_FILE").GetBoolean());
        Assert.Equal(0, document.RootElement.GetProperty("fixture_progress").GetInt32());
        var replay = await fixture.Adapters.ApplyRepairAsync(subject, selection, 0, proposal.ProposalDigest, DateTimeOffset.UtcNow, default);
        Assert.False(replay.IsNew); Assert.Equal(applied.OperationId, replay.OperationId);
    }
}
