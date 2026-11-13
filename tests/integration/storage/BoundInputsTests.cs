using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BoundInputsTests
{
    [Fact(DisplayName = "Independent Readers Reuse The Active Protected History And Reject A New Unregistered Entry")]
    public void ReusesActiveHistory()
    {
        using var fixture = new AndroidRuntimeFixture(configuration: true, launch: false, forceClose: false);
        using var accepted = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
        accepted.Prepared.Dispose();
        var source = fixture.Source;
        var registry = source.Lease.BindAttemptReads(source.Database);
        using var active = registry.Start(source.Instance, source.Database, new(), fixture.Accepted.JournalId);
        using var parents = JournalReadScope.HoldParents(source.Instance, new(), registry);
        var first = new JournalStore(source.Instance, new()).ReadAllRevisions(fixture.Accepted.JournalId);
        var second = new JournalStore(source.Instance, new()).ReadAllRevisions(fixture.Accepted.JournalId);
        Assert.Same(first[0], second[0]);
        var directory = Path.Combine(source.Instance.RuntimeRoot, "journals", fixture.Accepted.JournalId.ToString("D"));
        Assert.Throws<IOException>(() => File.WriteAllBytes(Path.Combine(directory, "0000000001.json"), [7]));
        var unknown = Path.Combine(directory, "unknown.json");
        File.WriteAllBytes(unknown, [9]);
        try { Assert.Throws<IOException>(() => new JournalStore(source.Instance, new()).Read(fixture.Accepted.JournalId)); }
        finally { File.Delete(unknown); }
        Assert.Same(first[0], new JournalStore(source.Instance, new()).ReadAllRevisions(fixture.Accepted.JournalId)[0]);
        fixture.VerifyOriginal();
    }

    [Fact(DisplayName = "A Parent History Read Keeps Frozen Plan Inputs Protected Until Its Scope Ends")]
    public void ProtectsParentInputs()
    {
        using var fixture = new AndroidRuntimeFixture(configuration: true, launch: false, forceClose: false);
        using var accepted = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
        var reference = accepted.Prepared.Attempt.Reference; accepted.Prepared.Dispose();
        var source = fixture.Source;
        var journal = new RecoveryJournalRegistry(source.Database).ReadJournal(fixture.Accepted.JournalId);
        var path = Path.Combine(source.Instance.RuntimeRoot, reference.RelativePath);
        var before = File.ReadAllBytes(path);
        using (JournalReadScope.HoldParents(source.Instance, new()))
        {
            var plan = AttemptPreparedBinding.ReadPlan(source.Instance, new(), journal);
            Assert.Equal(journal.OperationId.ToString("D"), plan.OperationId);
            Assert.Equal(plan.AttemptId, AttemptPreparedBinding.ReadPlan(source.Instance, new(), journal).AttemptId);
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [9, 7, 3]));
        }
        using var available = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var bytes = new byte[checked((int)available.Length)]; available.ReadExactly(bytes);
        Assert.Equal(before, bytes); fixture.VerifyOriginal();
    }

    [Fact(DisplayName = "One Fresh Bound Read Holds The Actual Attempt Total Definition And Configuration Files Until It Ends")]
    public void HoldsBoundInputs()
    {
        using var fixture = new AndroidRuntimeFixture(configuration: true, launch: false, forceClose: false);
        using var accepted = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
        var reference = accepted.Prepared.Attempt.Reference;
        accepted.Prepared.Dispose();
        var source = fixture.Source;
        var codec = new AttemptPlanCodec(source.Instance, new(), new(source.Instance, new()));
        var bound = codec.ReadBound(reference);
        try
        {
            var references = new[] { reference, AttemptPlanCodec.Reference(bound.Plan.ExecutionPlan),
                new BlobReference(bound.Total.DefinitionBundle.RelativePath, bound.Total.DefinitionBundle.SizeBytes, bound.Total.DefinitionBundle.Sha256) }
                .Concat(bound.Plan.ConfigurationPlans.Select(item => AttemptPlanCodec.Reference(item.Plan))).ToList();
            Assert.NotEmpty(bound.Plan.ConfigurationPlans);
            Assert.NotNull(bound.Plan.StartRequest);
            references.Add(AttemptPlanCodec.Reference(bound.Plan.StartRequest!));
            var paths = references.Distinct().Select(item => Path.Combine(source.Instance.RuntimeRoot, item.RelativePath)).ToArray();
            var original = paths.ToDictionary(path => path, File.ReadAllBytes);
            foreach (var path in paths)
                Assert.Throws<IOException>(() => { using var denied = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); });
            bound.Dispose(); bound.Dispose();
            Assert.Throws<ObjectDisposedException>(() => bound.Plan);
            Assert.Throws<ObjectDisposedException>(() => bound.Total);
            foreach (var path in paths)
            {
                using var available = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                var bytes = new byte[checked((int)available.Length)]; available.ReadExactly(bytes);
                Assert.Equal(original[path], bytes);
            }
            fixture.VerifyOriginal();
        }
        finally { bound.Dispose(); }
    }
}
