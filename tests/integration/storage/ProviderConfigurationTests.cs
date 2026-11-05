using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ProviderConfigurationTests
{
    [Fact(DisplayName = "A Verified Provider Writes Its Independent Saved Resource And Full Stops Before Original Identity And Bytes Return")]
    public async Task RestoresProviderResources()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("configuration", configuration: true);
        var source = fixture.Source; var original = File.ReadAllBytes(source.Configuration); var identity = PhysicalObjectIdentity.Read(source.Configuration);
        var receipt = await fixture.RunAsync();
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.NotApplicable, receipt.Facts.Business); Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(original, File.ReadAllBytes(source.Configuration)); Assert.Equal(identity, PhysicalObjectIdentity.Read(source.Configuration));
        var saved = new ConfigurationRevisionStore(source.Instance, source.Database, new()).Read(fixture.Address!)!;
        Assert.Equal(2, saved.Revision);
        Assert.Equal(ProviderConfigurationPolicy.Contract("provider-fixture", "main", new("provider-main", 0, 1)), saved.Contract);
        var entry = Assert.Single(saved.Tree.Entries, entry => entry.Kind == FileObjectKind.File);
        Assert.Contains("\"providerChanged\":true", File.ReadAllText(Path.Combine(source.Instance.RuntimeRoot, $"blobs/content/{entry.Sha256}.bin")));
        var state = fixture.State; var main = Assert.Single(state.Steps);
        var child = Assert.Single(state.Attempt.ConfigurationPlans);
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var plan = new ConfigurationPlanCodec(source.Instance, new(), blobs).ReadStructure(AttemptPlanCodec.Reference(child.Plan));
        var closures = new JobClosureStore(source.Instance, source.Database, new());
        var parentClosure = closures.Read(state.Journal.JournalId, main.WriterId, main.JobName, main.SessionId)!;
        var childClosure = closures.Read(plan.JournalId, main.WriterId, main.JobName, main.SessionId)!;
        Assert.True(parentClosure.WasExecuted); Assert.True(childClosure.WasExecuted);
        Assert.Equal(parentClosure.Root, childClosure.Root); Assert.Equal(parentClosure.Family.ToArray(), childClosure.Family.ToArray());
        Assert.Equal(new[] { state.Journal.JournalId }, parentClosure.JournalIds);
        Assert.Equal(new[] { plan.JournalId }, childClosure.JournalIds);
        Assert.All(parentClosure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        var journal = new JournalStore(source.Instance, new()).Read(plan.JournalId);
        var output = Assert.Single(journal.Root.GetProperty("participants").EnumerateArray(), participant =>
            participant.GetProperty("identity").TryGetProperty("format", out var format) && format.GetString() == "nexus.configuration.edit-output-reference");
        var reference = new EditOutputCodec(source.Instance, new(), blobs).ReadReference(output.GetProperty("identity"));
        using var held = blobs.OpenVerified(reference); var bytes = new byte[checked((int)held.Length)]; held.ReadExactly(bytes);
        var sealedOutput = new ContractReader().Read<ConfigurationEditOutput>(bytes, "configuration-edit-output.schema.json");
        var raw = Assert.Single(sealedOutput.Output.Entries, entry => entry.Kind == "file");
        Assert.Contains("\"providerChanged\":true", File.ReadAllText(Path.Combine(source.Instance.RuntimeRoot, $"blobs/content/{raw.Sha256}.bin")));
        Assert.Equal(JournalPhase.Committed, journal.Phase);
        ConfigurationRunInventory.VerifyReadOnly(source.Instance, source.Database, new());
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
    }

    [Fact(DisplayName = "The Same Address With A Generic Contract Cannot Be Borrowed By The Verified Provider Or Write Any Plan")]
    public async Task RejectsBorrowedContracts()
    {
        await using var fixture = await ProviderRuntimeFixture.PrepareAsync("configuration", configuration: true, invalidContract: true);
        var before = fixture.Source.Snapshot();
        using var draft = fixture.Service.DryRun(fixture.Source.Request(new BindingTarget(fixture.Source.OtherBinding)), DateTimeOffset.UtcNow);
        Assert.Null(draft.ReadPlan()); Assert.Contains(draft.Explanation.Items, item => item.ReasonCode == "configuration_required");
        Assert.Equal(before.Rows, fixture.Source.Snapshot().Rows); Assert.Equal(before.Files, fixture.Source.Snapshot().Files);
        Assert.Equal(0, fixture.Base.Count("journal_writer_jobs")); Assert.False(File.Exists(fixture.Marker));
    }
}
