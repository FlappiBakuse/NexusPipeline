using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ImmutableEvidenceTests
{
    [Fact(DisplayName = "Protected Evidence Keeps Invocation And Instance Bindings And Rechecks Bytes After The Last Reader")]
    public async Task PreservesEvidenceBindings()
    {
        using var fixture = new StepWriterFixture("tail");
        var source = fixture.Base.Source;
        var state = fixture.Scope.VerifyReadOnly();
        var binding = StepResultCodec.Binding(state.Latest.Root, Assert.Single(state.Steps));
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); collector.Start();
        Assert.Equal(37, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        var proof = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
        var artifact = proof.Use(binding, value => value.Artifact);
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var before = source.Snapshot();
        using (blobs.OpenVerified(artifact))
        {
            var document = StepEvidenceCodec.Read(blobs, binding, artifact);
            Assert.Equal(document, StepEvidenceCodec.Read(blobs, binding, artifact));
            var foreign = binding with { WriterId = Guid.NewGuid() };
            Assert.Throws<JsonException>(() => StepEvidenceCodec.Read(blobs, foreign, artifact));
            using var rawProtection = blobs.OpenVerified(document.RawOutput!);
            var raw = RawOutputCodec.Read(blobs, binding, document.RawOutput!);
            Assert.Equal(raw.ObservedBytes, RawOutputCodec.Read(blobs, binding, document.RawOutput!).ObservedBytes);
            Assert.Equal(raw.Parts.ToArray(), RawOutputCodec.Read(blobs, binding, document.RawOutput!).Parts.ToArray());
            Assert.Throws<JsonException>(() => RawOutputCodec.Read(blobs, foreign, document.RawOutput!));
        }
        var after = source.Snapshot();
        Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);

        var instance = new InstanceRoot(new()).OpenOrCreate(source.Root + "-evidence");
        var isolated = new ImmutableBlobStore(instance, new());
        var codec = new JobClosureCodec(instance, new(), isolated);
        var closure = stopped.Closure with { InstanceId = instance.InstanceId };
        var reference = codec.Write(closure);
        var path = Path.Combine(instance.RuntimeRoot, reference.RelativePath);
        var bytes = File.ReadAllBytes(path);
        using (isolated.OpenVerified(reference))
        {
            Assert.Equal(closure.JobName, codec.Read(reference).JobName);
            Assert.Equal(closure.Family.ToArray(), codec.Read(reference).Family.ToArray());
            Assert.Throws<IOException>(() => new JobClosureCodec(instance with { InstanceId = Guid.NewGuid() }, new(), isolated).Read(reference));
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, bytes));
            Assert.Throws<IOException>(() => File.Delete(path));
        }
        bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        Assert.Throws<IOException>(() => codec.Read(reference));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
