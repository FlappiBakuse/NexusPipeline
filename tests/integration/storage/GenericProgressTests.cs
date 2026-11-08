using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Automation;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class GenericProgressTests
{
    [Theory(DisplayName = "Actual Ordinary Retries Continue Checkpoint Zero To One To Two And Restore The Original Scene")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuesActualCheckpoint(bool saveAtFinish)
    {
        string marker = "";
        using var fixture = new ExecutionRuntimeFixture(configuration: true,
            judgement: new(AutomationJudgementLanguage.Keywords, ["done"], [], null), limits: new(2, 2, 120), captureFiles: false,
            configure: owner =>
            {
                var source = owner.Source; var catalog = source.Definitions.Read();
                marker = Path.Combine(source.Root, "checkpoint-inputs");
                var script = catalog.Scripts.Single(item => item.Id == source.ScriptId);
                var change = new AutomationChanges(catalog.Revision, [], [script with {
                    Arguments = ["process-output", "configuration-checkpoint", marker, source.Configuration],
                    UpdateConfigurationAtFinish = saveAtFinish }], [],
                    catalog.Bindings.Where(item => item.Id != source.BindingA && item.ScriptId == source.ScriptId)
                        .Select(item => item with { IsEnabled = false }).ToImmutableArray(), [], [], []);
                source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "generic-progress", change), change, DateTimeOffset.UtcNow);
            });
        var original = File.ReadAllBytes(fixture.Source.Configuration);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(new[] { "0", "1" }, File.ReadAllLines(marker));
        Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
        var source = fixture.Source;
        var path = source.Definitions.Read().Scripts.Single(item => item.Id == source.ScriptId).ConfigurationPaths.Single();
        var saved = new ConfigurationRevisionStore(source.Instance, source.Database, new()).Read(new(source.BindingA, GenericConfigurationPolicy.ResourceId(path)))!;
        var entry = Assert.Single(saved.Tree.Entries);
        using var content = new ImmutableBlobStore(source.Instance, new()).OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
        using var document = JsonDocument.Parse(content);
        Assert.Equal(saveAtFinish ? 2 : 0, document.RootElement.TryGetProperty("checkpoint", out var checkpoint) ? checkpoint.GetInt32() : 0);
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
    }
}
