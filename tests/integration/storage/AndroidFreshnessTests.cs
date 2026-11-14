using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidFreshnessTests
{
    [Fact(DisplayName = "An Owned Autocommit Reader Observes A Changed Actual Command Root And Cannot Outlive Its Scope")]
    public async Task RechecksCurrentRoots()
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false);
        await fixture.ExecuteAsync(async () =>
        {
            await fixture.RunAsync(); var state = fixture.State; var source = fixture.Source;
            var registration = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)[0];
            var startup = Assert.Single(AndroidStartupCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            var scope = new AndroidEvidenceScope(state.Total, source.Database);
            var reader = new AndroidControlEvidence(source.Instance, source.Database, new(), scope);
            try
            {
                var document = reader.Read<AndroidStartupDocument>(startup.Artifact);
                var reference = document.Commands.Single(item => item.Registration.Binding == registration.Binding).Observation;
                var original = reader.Observation(state.Total, registration, reference, verifyContent: false);
                Assert.NotNull(original.Root);
                var where = " WHERE journal_id='" + state.Journal.JournalId.ToString("D") + "' AND target_id='" + registration.Binding.TargetId.ToString("D") + "';";
                fixture.Base.Execute("UPDATE main.execution_target_job_roots SET image_sha256='" + new string('0', 64) + "'" + where);
                try { Assert.Throws<IOException>(() => reader.Observation(state.Total, registration, reference, verifyContent: false)); }
                finally { fixture.Base.Execute("UPDATE main.execution_target_job_roots SET image_sha256='" + original.Root!.ImageSha256 + "'" + where); }
                Assert.Equal(original, reader.Observation(state.Total, registration, reference, verifyContent: false));
                scope.Dispose();
                Assert.Throws<ObjectDisposedException>(() => reader.Request(state.Total, registration));
                Assert.Equal(registration.RequestDigest, new AndroidControlEvidence(source.Instance, source.Database, new())
                    .Digest(new AndroidControlEvidence(source.Instance, source.Database, new()).Request(state.Total, registration)));
                fixture.VerifyOriginal();
            }
            finally { scope.Dispose(); }
        });
    }
}
