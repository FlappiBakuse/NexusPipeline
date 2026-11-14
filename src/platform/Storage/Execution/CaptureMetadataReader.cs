using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record CaptureSavedEvidence(ExecutionCaptureState State, CaptureSourceDocument Source,
    CaptureRootDocument? Root, CaptureResultDocument? Result, FrozenJobClosure? Parent,
    ImmutableArray<BlobReference> References)
{
    internal ImmutableArray<CaptureCommandSaved> Commands { get; init; } = [];
}

internal sealed class CaptureMetadataReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    private ImmutableBlobStore? _blobs;
    private ImmutableBlobStore Blobs => _blobs ??= new(instance, contracts);

    internal ImmutableArray<CaptureSavedEvidence> Read(RegisteredJournal journal, RunAttemptPlan attempt,
        ExecutionPlan total, IReadOnlyList<JournalDocument> history, AndroidControlEvidence? androidReader = null)
    {
        RequireContext(journal, attempt, total, history);
        using var ownedAndroidScope = androidReader is null ? new AndroidEvidenceScope(total, database) : null;
        androidReader ??= new(instance, database, contracts, ownedAndroidScope);
        var appends = CaptureOriginalPrefix.Read(history, total, attempt.ExecutionPlan.Sha256);
        var saved = new Dictionary<Guid, CaptureSavedEvidence>();
        foreach (var append in appends)
        {
            var item = append.Intent;
            if (item.Stage == "source")
            {
                var source = ReadDocument<CaptureSourceDocument>(item.Artifact);
                RequireSource(item, source, append.Before, journal, attempt, total, androidReader);
                var sourceReferences = ImmutableArray.Create(item.Artifact, source.Window?.ReadyArtifact ?? source.Android!.Startup);
                if (source.Android?.MuMu is { } mumu) sourceReferences = sourceReferences.Add(mumu.InfoEvidence);
                saved.Add(item.Binding.CaptureId, new(new(item.Binding, item.Artifact, null, null), source, null, null, null, sourceReferences));
                continue;
            }
            if (!saved.TryGetValue(item.Binding.CaptureId, out var prior) || prior.State.Binding != item.Binding) throw Invalid();
            if (item.Stage == "root")
            {
                var root = ReadDocument<CaptureRootDocument>(item.Artifact);
                RequireRoot(item, prior, root, total);
                saved[item.Binding.CaptureId] = prior with {
                    State = prior.State with { Root = item.Artifact }, Root = root, References = prior.References.Add(item.Artifact) };
                continue;
            }
            if (MuMuCaptureSequence.IsChild(item.Stage))
            {
                var evidence = new MuMuCaptureEvidence(Blobs);
                var commands = prior.Commands;
                var childReferences = prior.References.Add(item.Artifact);
                if (MuMuCaptureSequence.IsRoot(item.Stage)) commands = commands.Add(evidence.ReadRoot(item, prior, total));
                else
                {
                    if (commands.IsEmpty) throw Invalid();
                    var observed = evidence.ReadObserved(item, commands[^1]);
                    commands = commands.SetItem(commands.Length - 1, commands[^1] with {
                        ObservationArtifact = item.Artifact, Observation = observed });
                    childReferences = childReferences.AddRange(MuMuCaptureEvidence.References(observed));
                }
                saved[item.Binding.CaptureId] = prior with { Commands = commands, References = childReferences }; continue;
            }
            var result = ReadDocument<CaptureResultDocument>(item.Artifact);
            var parent = RequireResult(item, prior, result, journal, attempt, total, append.Before);
            var references = prior.References.Add(item.Artifact).Add(result.ParentClosure);
            if (result.Png is { } png) references = references.Add(png);
            if (result.Frames is { } frames) references = references.AddRange(frames.Select(frame => frame.Png));
            saved[item.Binding.CaptureId] = prior with {
                State = prior.State with { Result = item.Artifact }, Result = result, Parent = parent, References = references };
        }
        return saved.Values.OrderBy(value => total.Steps.ToList().FindIndex(step => step.StepId == value.State.Binding.Primary.StepId))
            .ToImmutableArray();
    }

    private void RequireSource(ExecutionCaptureIntent item, CaptureSourceDocument value, JournalDocument before,
        RegisteredJournal journal, RunAttemptPlan attempt, ExecutionPlan total, AndroidControlEvidence androidReader)
    {
        Header(value.Format, value.Version, value.Binding, item.Binding, "-source");
        var primary = item.Binding.Primary;
        var step = total.Steps.Single(step => step.StepId == primary.StepId);
        var pc = step.Target?.Pc;
        if (value.Window is null)
        {
            RequireAndroidSource(item, value, before, attempt, total, androidReader);
            RequirePrimarySource(item, value, before, journal, attempt, total); return;
        }
        if (pc is null || value.Android is not null) throw Invalid();
        var runtime = total.CaptureRuntime ?? throw Invalid();
        if (value.Window is not { } window || value.HelperImage is null || value.Creator is null ||
            !Same(value.HelperImage, runtime.Broker) || window.Binding != new PcStageBinding(
                primary.InstanceId, primary.ParentJournalId, primary.ParentOperationId, primary.AttemptId, primary.StepId, item.Binding.TotalPlanSha256) ||
            window.WindowHandle <= 0 || window.Width is < 1 or > 8192 || window.Height is < 1 or > 8192 ||
            (long)window.Width * window.Height > 33554432 || !SameImage(window.Process, pc.ReadyImage)) throw Invalid();
        TargetJobEvidence.RequireIdentity(window.Process);
        if (DispatchIntentCodec.Read(before.Root, total) is not null) throw Invalid();
        var stages = PcStageCodec.Read(before.Root, total, attempt.ExecutionPlan.Sha256);
        var stage = stages.SingleOrDefault(stage => stage.Binding == window.Binding && stage.Artifact == window.ReadyArtifact) ?? throw Invalid();
        var evidence = ReadDocument<PcStageDocument>(stage.Artifact);
        _ = new PcStageEvidence(instance, database, contracts).RequireAll(before.Root, total, attempt.ExecutionPlan.Sha256);
        if (stage.Outcome != window.Ownership || evidence.Outcome != window.Ownership) throw Invalid();
        if (window.Ownership == TargetExecutionOutcome.Skipped)
        {
            if (step.Target!.LaunchByHost || !evidence.ReadyMembers.IsEmpty) throw Invalid();
        }
        else if (window.Ownership is not (TargetExecutionOutcome.ExternalReady or TargetExecutionOutcome.OwnedReady) ||
            !evidence.ReadyMembers.Contains(window.Process)) throw Invalid();
        RequirePrimarySource(item, value, before, journal, attempt, total);
    }

    private void RequirePrimarySource(ExecutionCaptureIntent item, CaptureSourceDocument value, JournalDocument before,
        RegisteredJournal journal, RunAttemptPlan attempt, ExecutionPlan total)
    {
        if (value.Creator is null || DispatchIntentCodec.Read(before.Root, total) is not null) throw Invalid();
        var context = new StepEvidenceReader(instance, database, contracts)
            .ContextFromValidatedRead(item.Binding.Primary, journal, attempt, total, before);
        if (context.Root is null || context.Job.ClosedElapsedMilliseconds is not null) throw Invalid();
        RequireCreator(item.Binding.Primary, value.Creator);
    }

    private void RequireAndroidSource(ExecutionCaptureIntent item, CaptureSourceDocument value, JournalDocument before,
        RunAttemptPlan attempt, ExecutionPlan total, AndroidControlEvidence androidReader)
    {
        var primary = item.Binding.Primary;
        var step = total.Steps.Single(step => step.StepId == primary.StepId);
        var target = step.Target?.Android ?? throw Invalid();
        var android = value.Android ?? throw Invalid();
        if (step.Target!.Kind != "android" || target.Driver is not ("genericAdb" or "mumu") || step.Target.Pc is not null ||
            total.CaptureRuntime is null || value.HelperImage is null ||
            android.Binding != new AndroidControlBinding(primary.InstanceId, primary.ParentJournalId,
                primary.ParentOperationId, primary.AttemptId, primary.StepId, item.Binding.TotalPlanSha256) ||
            android.Endpoint != target.Endpoint || android.Package != target.Package) throw Invalid();
        if (target.Driver == "genericAdb")
        {
            if (android.MuMu is not null || !Same(value.HelperImage, target.Tool)) throw Invalid();
        }
        else
        {
            var mapping = target.Mapping ?? throw Invalid(); var saved = android.MuMu ?? throw Invalid();
            if (!Same(value.HelperImage, total.CaptureRuntime.Broker) || saved.Index != mapping.Index ||
                saved.EndpointPort != mapping.EndpointPort || saved.ManagerImageSha256 != target.Tool.Sha256 ||
                saved.InfoEvidence != new BlobReference(mapping.InfoEvidence.RelativePath,
                    mapping.InfoEvidence.SizeBytes, mapping.InfoEvidence.Sha256)) throw Invalid();
        }
        var startup = AndroidStartupCodec.Read(before.Root, total, attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(stage => stage.Binding == android.Binding && stage.Artifact == android.Startup) ?? throw Invalid();
        if (startup.Outcome is not (TargetExecutionOutcome.Skipped or TargetExecutionOutcome.ControlledReady) ||
            AndroidCollectedCodec.Read(before.Root, total, attempt.ExecutionPlan.Sha256)
                .Any(stage => stage.Binding.StepId == primary.StepId) ||
            AndroidControlCodec.Read(before.Root, total, attempt.ExecutionPlan.Sha256)
                .Any(command => command.Binding.StepId == primary.StepId && AndroidControlAdmission.IsCleanup(command.Purpose)))
            throw Invalid();
        _ = new AndroidResultEvidence(instance, database, contracts, androidReader)
            .RequireStartup(before.Root, total, attempt.ExecutionPlan.Sha256, startup, verifyContent: false);
    }

    private static void RequireRoot(ExecutionCaptureIntent item, CaptureSavedEvidence prior, CaptureRootDocument value, ExecutionPlan total)
    {
        Header(value.Format, value.Version, value.Binding, item.Binding, "-root");
        if (prior.Source.Android?.MuMu is not null &&
            total.Steps.Single(step => step.StepId == item.Binding.Primary.StepId).Target?.Android?.ScreenshotTool is null) throw Invalid();
        TargetJobEvidence.RequireIdentity(value.NativeRoot);
        if (prior.Root is not null || value.Source != prior.State.Source ||
            !SameImage(value.NativeRoot, prior.Source.HelperImage) ||
            value.NativeRoot.UserSid != prior.Source.Creator.Process.UserSid ||
            value.NativeRoot.IsElevated != prior.Source.Creator.Process.IsElevated ||
            value.NativeRoot == prior.Source.Window?.Process || value.NativeRoot == prior.Source.Creator.Process) throw Invalid();
    }

    private FrozenJobClosure RequireResult(ExecutionCaptureIntent item, CaptureSavedEvidence prior,
        CaptureResultDocument value, RegisteredJournal journal, RunAttemptPlan attempt, ExecutionPlan total, JournalDocument latest)
    {
        Header(value.Format, value.Version, value.Binding, item.Binding, "-result");
        if (prior.Result is not null || value.Source != prior.State.Source || value.Root != prior.State.Root ||
            value.Outcome is not ("captured" or "unavailable")) throw Invalid();
        var primary = item.Binding.Primary;
        var reader = new StepEvidenceReader(instance, database, contracts);
        var context = reader.ContextFromValidatedRead(primary, journal, attempt, total, latest);
        var parent = new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(
            primary.ParentJournalId, primary.WriterId, primary.JobName, primary.SessionId) ?? throw Invalid();
        if (value.ParentClosure is null || reader.ClosureReference(primary) != value.ParentClosure ||
            parent.InstanceId != primary.InstanceId || parent.OperationId != primary.ParentOperationId ||
            parent.Creator != prior.Source.Creator.Process || parent.WriterId != primary.WriterId ||
            parent.Root != context.Root || !parent.WasExecuted || parent.Root is null ||
            parent.RunElapsedMilliseconds != context.Job.ClosedElapsedMilliseconds || context.Job.ClosedElapsedMilliseconds is null)
            throw Invalid();
        if (prior.Root is not null && !parent.Family.Contains(prior.Root.NativeRoot)) throw Invalid();
        if (value.HelperStopped is { } stopped)
        {
            TargetJobEvidence.RequireInventory(stopped);
            if (stopped.ActiveProcesses != 0 || !stopped.LivingFamily.IsEmpty || stopped.LimitFlags != 0x00002000 ||
                stopped.AllFamily.Any(member => !parent.Family.Contains(member)) ||
                prior.Root is not null && !stopped.AllFamily.Contains(prior.Root.NativeRoot)) throw Invalid();
        }
        if (prior.Source.Window is not null && value.Image is not null ||
            value.Outcome == "unavailable" && value.Image is not null) throw Invalid();
        if (value.Outcome == "captured")
        {
            if (prior.Source.Android is not null)
            {
                if (value.Image is null) throw Invalid();
                BoundedCapturePng.RequireInfo(value.Image);
            }
            var frameCount = total.CaptureRuntime?.MaximumFramesPerStep ?? throw Invalid();
            if (prior.Source.Window is not null && frameCount > 1)
            {
                if (value.Png is not null || value.Frames is null) throw Invalid();
                CaptureFramePolicy.RequireBatch(value.Frames, item.Binding.CaptureId, checked((int)frameCount),
                    prior.Source.Window.Width, prior.Source.Window.Height);
            }
            else if (value.Frames is not null || value.Png is not { SizeBytes: >= 57 and <= ExecutionCaptureInputs.MaximumPngBytes } ||
                value.Png.RelativePath != $"blobs/content/{value.Png.Sha256}.bin" ||
                !NexusPipeline.Core.Configuration.FileTreePolicy.Digest(value.Png.Sha256)) throw Invalid();
            if (DispatchIntentCodec.Read(latest.Root, total) is not null ||
                prior.Root is null || value.HelperStopped is not { TotalProcesses: > 0 } ||
                value.ReasonCode != "execution_capture_frame_verified") throw Invalid();
        }
        else if (value.Png is not null || value.Frames is not null || value.ReasonCode is not ("execution_capture_unavailable" or "execution_capture_context_closed" or
            "execution_capture_deadline" or "execution_capture_helper_failed" or "execution_capture_output_rejected" or
            "execution_capture_output_incomplete" or "execution_capture_frame_rejected" or "execution_capture_recovered_unavailable" or
            "execution_capture_tool_unconfigured" or "execution_capture_mapping_unverified") ||
            value.HelperStopped is null && value.ReasonCode != "execution_capture_recovered_unavailable") throw Invalid();
        if (value.ReasonCode == "execution_capture_tool_unconfigured" &&
            (prior.Source.Android?.MuMu is null || prior.Root is not null || !prior.Commands.IsEmpty ||
                value.HelperStopped is not { TotalProcesses: 0 } ||
                total.Steps.Single(step => step.StepId == primary.StepId).Target?.Android?.ScreenshotTool is not null)) throw Invalid();
        MuMuCaptureEvidence.RequireCollected(prior with { Parent = parent }, value);
        return parent;
    }

    private void RequireCreator(StepEvidenceBinding primary, JournalWriter creator)
    {
        TargetJobEvidence.RequireIdentity(creator.Process);
        if (creator.WriterId != primary.WriterId) throw Invalid();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT process_id,created_file_time,image_path,image_sha256,user_sid,elevated FROM main.journal_writers " +
            "WHERE journal_id=$journal AND writer_id=$writer;";
        command.Parameters.AddWithValue("$journal", primary.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", primary.WriterId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || creator.Process != new ProcessIdentity(row.GetInt32(0), DateTime.FromFileTimeUtc(row.GetInt64(1)),
            row.GetString(2), row.GetString(3), row.GetString(4), row.GetBoolean(5))) throw Invalid();
    }

    private void RequireContext(RegisteredJournal journal, RunAttemptPlan attempt, ExecutionPlan total, IReadOnlyList<JournalDocument> history)
    {
        if (history.Count == 0 || journal.JournalId.ToString("D") != attempt.JournalId ||
            journal.OperationId.ToString("D") != attempt.OperationId || journal.PlanDigest != history[^1].PlanDigest ||
            attempt.InstanceId != instance.InstanceId.ToString("D") || total.InstanceId != attempt.InstanceId || total.RunId != attempt.RunId ||
            history.Any(document => document.InstanceId != instance.InstanceId || document.Kind != NexusPipeline.Core.Configuration.JournalKind.RunAttempt ||
                document.JournalId != journal.JournalId || document.PlanDigest != journal.PlanDigest || document.PlanPath != journal.PlanPath ||
                document.Root.GetProperty("operationId").GetGuid() != journal.OperationId) ||
            !history.Any(document => document.Revision == journal.Revision && document.Phase == journal.Phase)) throw Invalid();
        ExecutionCaptureInputs.Require(total);
    }

    private T ReadDocument<T>(BlobReference reference)
    {
        StepEvidenceCodec.Plan(reference);
        using var input = Blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var json = StrictJson.Parse(bytes);
        var result = json.RootElement.Deserialize<T>(StepResultCodec.Options) ?? throw Invalid();
        if (!Same(json.RootElement, JsonSerializer.SerializeToElement(result, StepResultCodec.Options))) throw Invalid();
        return result;
    }

    private static void Header(string format, int version, ExecutionCaptureBinding binding, ExecutionCaptureBinding expected, string suffix)
    { if (format != ExecutionCaptureCodec.Format + suffix || version != 1 || binding != expected) throw Invalid(); }
    private static bool Same<T>(T first, T second) => JsonElement.DeepEquals(
        JsonSerializer.SerializeToElement(first, StepResultCodec.Options), JsonSerializer.SerializeToElement(second, StepResultCodec.Options));
    private static bool SameImage(ProcessIdentity? process, ExecutionPlanInterpreterImage image) => process is not null &&
        StringComparer.OrdinalIgnoreCase.Equals(process.ImagePath, image.Path) && process.ImageSha256 == image.Sha256;
    internal static IOException Invalid() => new("execution_capture_evidence_unbound");
}
