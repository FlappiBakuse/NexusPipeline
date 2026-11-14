using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidScopeTests
{
    [Theory(DisplayName = "Repeated Scoped Decoding Still Requires The Actual Current File Bytes And Clr Shape")]
    [InlineData("locked")]
    [InlineData("changed")]
    [InlineData("other-type")]
    public void RechecksDocumentBytes(string fault)
    {
        using var fixture = new AndroidRuntimeFixture(launch: false, forceClose: false);
        var source = fixture.Source; var total = fixture.State.Total;
        var reader = new AndroidControlEvidence(source.Instance, source.Database, new(), new(total));
        var document = new AndroidControlPlan(AndroidControlCodec.ControlPlanFormat, 1,
            source.Instance.InstanceId, fixture.Accepted.JournalId, Guid.NewGuid(), Guid.NewGuid(), "fixture-step", new string('a', 64),
            AndroidControlCapacity.Derive(Assert.Single(total.Steps)));
        var original = JsonSerializer.SerializeToUtf8Bytes(document, StepResultCodec.Options);
        var reference = new ImmutableBlobStore(source.Instance, new()).PersistPlan(original);
        Assert.Equal(document, reader.Read<AndroidControlPlan>(reference));
        var path = Path.Combine(source.Instance.RuntimeRoot, reference.RelativePath);
        if (fault == "locked")
        {
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Throws<IOException>(() => reader.Read<AndroidControlPlan>(reference));
        }
        else if (fault == "changed")
        {
            var changed = original.ToArray(); changed[^2] ^= 1;
            try
            {
                File.WriteAllBytes(path, changed);
                Assert.Throws<IOException>(() => reader.Read<AndroidControlPlan>(reference));
            }
            finally { File.WriteAllBytes(path, original); }
        }
        else Assert.Throws<JsonException>(() => reader.Read<AndroidStartupDocument>(reference));
    }

    [Fact(DisplayName = "Reusing Scoped Command Text And Pure Digests Cannot Hide A Changed Actual Sql Subject")]
    public async Task RechecksSqlSubjects()
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false);
        await fixture.ExecuteAsync(async () =>
        {
            await fixture.RunAsync(); var state = fixture.State; var source = fixture.Source;
            var registration = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)[0];
            using var scope = new AndroidEvidenceScope(state.Total, source.Database);
            var reader = new AndroidControlEvidence(source.Instance, source.Database, new(), scope);
            var before = reader.Request(state.Total, registration);
            Assert.Equal(AndroidRuntimeFixture.Subject, before.SubjectId);
            Assert.NotEmpty(before.Tool.RuntimeFiles);
            Assert.Throws<NotSupportedException>(() => ((IList<NexusPipeline.Extensibility.Protocol.ExecutionPlanRuntimeFile>)before.Tool.RuntimeFiles).Clear());
            var derived = scope.Derive(state.Total, registration, before.SubjectId);
            Assert.Throws<NotSupportedException>(() => ((IList<NexusPipeline.Extensibility.Protocol.ExecutionPlanRuntimeFile>)derived.Tool.RuntimeFiles).Clear());
            var foreign = before with { Tool = before.Tool with { RuntimeFiles = before.Tool.RuntimeFiles.ToList() } };
            var originalDigest = reader.Digest(foreign);
            ((IList<NexusPipeline.Extensibility.Protocol.ExecutionPlanRuntimeFile>)foreign.Tool.RuntimeFiles).Clear();
            Assert.NotEqual(originalDigest, reader.Digest(foreign));
            var where = " WHERE operation_id='" + state.Journal.OperationId.ToString("D") + "';";
            fixture.Base.Execute("UPDATE main.operations SET subject_id='changed-current-subject'" + where);
            try { Assert.Throws<IOException>(() => reader.Request(state.Total, registration)); }
            finally { fixture.Base.Execute("UPDATE main.operations SET subject_id='" + AndroidRuntimeFixture.Subject + "'" + where); }
            Assert.Equal(before, reader.Request(state.Total, registration)); fixture.VerifyOriginal();
        });
    }
}
