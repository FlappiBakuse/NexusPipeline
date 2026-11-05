using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BoundInputsTests
{
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
