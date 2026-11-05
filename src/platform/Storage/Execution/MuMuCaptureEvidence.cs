using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class MuMuCaptureEvidence(ImmutableBlobStore blobs)
{
    internal CaptureCommandSaved ReadRoot(ExecutionCaptureIntent intent, CaptureSavedEvidence prior,
        ExecutionPlan total)
    {
        var sequence = MuMuCaptureSequence.Sequence(intent.Stage);
        var value = Document<CaptureCommandRoot>(intent.Artifact);
        if (prior.Source.Android?.MuMu is null || prior.State.Root is null || prior.Root is null ||
            value.Format != ExecutionCaptureCodec.Format + "-command-root" || value.Version != 1 ||
            value.Binding != intent.Binding || value.Source != prior.State.Source || value.BrokerRoot != prior.State.Root ||
            value.Sequence != sequence || value.Purpose != MuMuCaptureSequence.Purpose(sequence) ||
            !ExecutionCaptureCodec.JobName(value.JobName) || value.JobName == intent.Binding.HelperJobName ||
            value.JobName == intent.Binding.Primary.JobName ||
            prior.Commands.Any(command => command.Root.JobName == value.JobName || command.Root.Process == value.Process))
            throw Invalid();
        var expected = MuMuCaptureSequence.Image(total, intent.Binding, sequence);
        TargetJobEvidence.RequireIdentity(value.Process);
        if (!StringComparer.OrdinalIgnoreCase.Equals(value.Process.ImagePath, expected.Path) ||
            value.Process.ImageSha256 != expected.Sha256 ||
            value.Process.UserSid != prior.Source.Creator.Process.UserSid ||
            value.Process.IsElevated != prior.Source.Creator.Process.IsElevated ||
            value.Process == prior.Root.NativeRoot || value.Process == prior.Source.Creator.Process) throw Invalid();
        return new(intent.Artifact, value, null, null);
    }

    internal CaptureCommandObserved ReadObserved(ExecutionCaptureIntent intent, CaptureCommandSaved root)
    {
        var value = Document<CaptureCommandObserved>(intent.Artifact);
        if (value.Format != ExecutionCaptureCodec.Format + "-command-observed" || value.Version != 1 ||
            value.Binding != intent.Binding || value.CommandRoot != root.RootArtifact ||
            root.Observation is not null || root.Root.Sequence != MuMuCaptureSequence.Sequence(intent.Stage) ||
            value.Completion is not ("natural-root-exit-and-eof-before-stop" or "unverified") ||
            value.Completion == "natural-root-exit-and-eof-before-stop" && value.ExitCode is null) throw Invalid();
        var image = root.Root.Sequence == 2;
        MuMuCaptureSequence.RequireOutput(value.StandardOutput,
            image ? (int)ExecutionCaptureInputs.MaximumPngBytes : MuMuCaptureSequence.MaximumInfoBytes, image);
        MuMuCaptureSequence.RequireOutput(value.StandardError, MuMuCaptureSequence.MaximumErrorBytes, binary: false);
        if (!image && value.StandardOutput.SizeBytes + value.StandardError.SizeBytes > MuMuCaptureSequence.MaximumInfoBytes)
            throw Invalid();
        TargetJobEvidence.RequireInventory(value.Stopped);
        if (value.Stopped.ActiveProcesses != 0 || !value.Stopped.LivingFamily.IsEmpty ||
            value.Stopped.TotalProcesses == 0 || value.Stopped.LimitFlags != 0x00002000 ||
            !value.Stopped.AllFamily.Contains(root.Root.Process)) throw Invalid();
        return value;
    }

    internal static IEnumerable<BlobReference> References(CaptureCommandObserved value) =>
        value.StandardOutput.Parts.Concat(value.StandardError.Parts);

    internal static void RequireCollected(CaptureSavedEvidence saved, CaptureResultDocument result)
    {
        var commands = saved.Commands;
        if (saved.Source.Android?.MuMu is null)
        {
            if (!commands.IsEmpty) throw Invalid();
            return;
        }
        foreach (var command in commands)
        {
            if (saved.Parent is { } parent && (!parent.Family.Contains(command.Root.Process) ||
                command.Observation?.Stopped.AllFamily.Any(member => !parent.Family.Contains(member)) == true)) throw Invalid();
            if (result.HelperStopped is { } inner && (!inner.AllFamily.Contains(command.Root.Process) ||
                command.Observation?.Stopped.AllFamily.Any(member => !inner.AllFamily.Contains(member)) == true)) throw Invalid();
        }
        if (result.Outcome != "captured") return;
        if (commands.Length != 3 || commands.Where((command, index) => command.Root.Sequence != index + 1).Any() ||
            commands.Any(command => command.Observation is not {
                Completion: "natural-root-exit-and-eof-before-stop", ExitCode: 0 })) throw Invalid();
        var image = commands[1].Observation!.StandardOutput;
        if (result.Png is null || image.SizeBytes != result.Png.SizeBytes || image.Sha256 != result.Png.Sha256) throw Invalid();
    }

    internal void RequireCapturedMapping(CaptureSavedEvidence saved)
    {
        if (saved.Source.Android?.MuMu is not { } original) return;
        if (saved.Result is not { Outcome: "captured" } result) throw Invalid();
        RequireCollected(saved, result);
        foreach (var command in saved.Commands.Where(command => command.Root.Sequence != 2))
        {
            var observed = command.Observation!;
            var stdout = ReadText(observed.StandardOutput, MuMuCaptureSequence.MaximumInfoBytes);
            var stderr = ReadText(observed.StandardError, MuMuCaptureSequence.MaximumErrorBytes);
            if (!string.IsNullOrWhiteSpace(stderr)) throw Invalid();
            MuMuInfoParser.RequireCaptureMapping(stdout, original.Index, saved.Source.Android.Endpoint);
        }
    }

    internal void RequirePredecessor(CaptureSavedEvidence saved, int nextSequence)
    {
        if (nextSequence is < 1 or > 3 || saved.Commands.Length != nextSequence - 1 ||
            saved.Commands.Any(command => command.Observation is not {
                Completion: "natural-root-exit-and-eof-before-stop", ExitCode: 0 })) throw Invalid();
        if (nextSequence == 1) return;
        var first = saved.Commands[0].Observation!;
        var original = saved.Source.Android?.MuMu ?? throw Invalid();
        if (!string.IsNullOrWhiteSpace(ReadText(first.StandardError, MuMuCaptureSequence.MaximumErrorBytes))) throw Invalid();
        MuMuInfoParser.RequireCaptureMapping(ReadText(first.StandardOutput, MuMuCaptureSequence.MaximumInfoBytes),
            original.Index, saved.Source.Android!.Endpoint);
    }

    private string ReadText(ExecutionCaptureOutput value, int maximum)
    {
        try { return new UTF8Encoding(false, true).GetString(MuMuCaptureSequence.ReadOutput(blobs, value, maximum)); }
        catch (DecoderFallbackException error) { throw new IOException("execution_capture_mumu_unbound", error); }
    }

    private T Document<T>(BlobReference reference)
    {
        StepEvidenceCodec.Plan(reference);
        using var input = blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var parsed = StrictJson.Parse(bytes);
        var value = parsed.RootElement.Deserialize<T>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(value, StepResultCodec.Options))) throw Invalid();
        return value;
    }

    private static IOException Invalid() => MuMuCaptureSequence.Invalid();
}
