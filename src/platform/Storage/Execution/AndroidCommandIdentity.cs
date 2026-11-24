using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace NexusPipeline.Platform.Storage.Execution;

internal enum AndroidCommandPurpose { InitialReadiness, Startup, AwaitReadiness, CleanupApplication, ShutdownDevice, ConfirmOffline, CleanupReadiness }

internal sealed record AndroidCommandRequest(ExecutionTargetBinding Binding, int Sequence,
    AndroidCommandPurpose Purpose, ExecutionAndroidAction Action, string SubjectId,
    string Endpoint, string Package, string Driver, ExecutionTargetAndroidMapping? Mapping,
    ExecutionPlanInterpreterImage Tool, ConfigurationEditorCommand Command, AndroidControlLimits Limits,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool OwnedHandoff = false);

internal static class AndroidCommandIdentity
{
    internal static string Digest(AndroidCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Binding is null || request.Binding.InstanceId == Guid.Empty || request.Binding.ParentJournalId == Guid.Empty ||
            request.Binding.ParentOperationId == Guid.Empty || request.Binding.AttemptId == Guid.Empty ||
            request.Binding.TargetId == Guid.Empty || string.IsNullOrWhiteSpace(request.Binding.StepId) ||
            !FileTreePolicy.Digest(request.Binding.TotalPlanSha256) || request.Sequence is < 0 or >= 1024 ||
            !Enum.IsDefined(request.Purpose) || !Enum.IsDefined(request.Action) ||
            string.IsNullOrWhiteSpace(request.SubjectId) || request.SubjectId.Length > 256 ||
            request.Tool is null || request.Command is null || request.Limits is null) throw Invalid();
        return Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(request, StepResultCodec.Options)));
    }

    internal static AndroidCommandRequest Derive(ExecutionPlan total, ExecutionTargetBinding actualBinding,
        string actualParentSubject, int sequence, AndroidCommandPurpose purpose, ExecutionAndroidAction action)
    {
        var step = total.Steps.Single(item => item.StepId == actualBinding.StepId);
        if (actualBinding.InstanceId.ToString("D") != total.InstanceId ||
            step.Target is not { Kind: "android", Android: not null, Pc: null } target ||
            !PurposeAllows(purpose, action) || !target.LaunchByHost && (purpose is
                AndroidCommandPurpose.InitialReadiness or AndroidCommandPurpose.Startup or AndroidCommandPurpose.AwaitReadiness))
            throw Invalid();
        var android = target.Android;
        var limits = AndroidControlCapacity.Derive(step);
        if (sequence < 0 || sequence >= limits.MaximumCommands || sequence >= 1024) throw Invalid();
        var parameters = ExecutionAndroidCommands.DeriveFrozen(android, action);
        var tool = android.Emulator is { } emulator && (action == ExecutionAndroidAction.LaunchDevice || action == ExecutionAndroidAction.Shutdown && emulator.Quit is not null)
            ? emulator.Control : android.Tool;
        var command = EditorCommandPolicy.Freeze(new(tool.Path, tool.Sha256,
            parameters.Arguments, Path.GetDirectoryName(tool.Path)!));
        var request = new AndroidCommandRequest(actualBinding with { }, sequence, purpose, action, actualParentSubject,
            android.Endpoint, android.Package, android.Driver, android.Mapping, tool, command, limits,
            action == ExecutionAndroidAction.LaunchDevice && android.Emulator is { Launch.Detached: true });
        var files = request.Tool.RuntimeFiles ?? throw Invalid();
        // Other request branches are sealed scalar records or ImmutableArray; only this generated list needs detaching.
        return request with { Tool = request.Tool with { RuntimeFiles = Array.AsReadOnly(files.ToArray()) } };
    }

    private static bool PurposeAllows(AndroidCommandPurpose purpose, ExecutionAndroidAction action) => purpose switch
    {
        AndroidCommandPurpose.InitialReadiness => action is ExecutionAndroidAction.DeviceState or ExecutionAndroidAction.Foreground,
        AndroidCommandPurpose.Startup => action is ExecutionAndroidAction.LaunchDevice or ExecutionAndroidAction.Connect or ExecutionAndroidAction.StartApplication,
        AndroidCommandPurpose.AwaitReadiness => action is ExecutionAndroidAction.DeviceState or ExecutionAndroidAction.Foreground,
        AndroidCommandPurpose.CleanupReadiness => action == ExecutionAndroidAction.DeviceState,
        AndroidCommandPurpose.CleanupApplication => action == ExecutionAndroidAction.StopApplication,
        AndroidCommandPurpose.ShutdownDevice => action == ExecutionAndroidAction.Shutdown,
        AndroidCommandPurpose.ConfirmOffline => action == ExecutionAndroidAction.OfflineProbe,
        _ => false
    };

    private static ArgumentException Invalid() => new("execution_android_command_request_invalid");
}

internal sealed record AndroidControlLimits(long StartupCommands, long CleanupCommands, long MaximumCommands,
    int MaximumCommandRawBytes, int MaximumBindingRawBytes, int MaximumObservedProcesses, long RegistrationEvents,
    int ResultEvents);

internal static class AndroidControlCapacity
{
    internal const int PollMilliseconds = 1000;
    internal const int MaximumCommandRawBytes = 2097152;
    internal const int MaximumBindingRawBytes = 20 * 1024 * 1024;
    internal const int MaximumObservedProcesses = 4096;
    internal const int MaximumOfflineSeconds = 60;

    internal static AndroidControlLimits Derive(ExecutionPlanStep step)
    {
        if (step.Target is not { Kind: "android", Android: not null, Pc: null } target ||
            target.WaitSeconds is < 0 or > 604800 || target.Android.Driver is not ("genericAdb" or "mumu"))
            throw new ArgumentException("execution_android_control_definition_invalid");
        var polls = checked(target.WaitSeconds + 1);
        var startup = !target.LaunchByHost ? 0 : checked(polls + (target.Android.Driver == "mumu" || target.Android.Emulator is not null ? 6 : 5));
        var cleanup = checked(1L + (target.Android.Driver == "mumu" ? 1 : 0) + (target.ForceCloseAtEnd ? 1L + MaximumOfflineSeconds + 1 : 0));
        var commands = checked(startup + cleanup);
        return new(startup, cleanup, commands, MaximumCommandRawBytes,
            MaximumBindingRawBytes, MaximumObservedProcesses, commands, 2 + (target.LaunchByHost && target.Android.Emulator is not null ? 2 : 0));
    }
}

internal enum AndroidResponseCompletion { Unverified, NaturalRootExitAndEofBeforeStop, OwnedLaunchHandoff }

internal sealed record AndroidCommandRegistration(string Format, int Version, ExecutionTargetBinding Binding,
    int Sequence, AndroidCommandPurpose Purpose, ExecutionAndroidAction Action, BlobReference Request,
    string RequestDigest, BlobReference ControlPlan, BlobReference? PreviousObservation);
internal sealed record AndroidCommandObservation(string Format, int Version, ExecutionTargetBinding Binding,
    string RequestDigest, ProcessIdentity? Root, int? ExitCode, bool StartReturned, bool OutputComplete,
    ImmutableArray<TargetCommandPart> Parts, EngineOutcome Engine, long? ElapsedMilliseconds,
    AndroidResponseCompletion Completion);
internal sealed record AndroidCommandCollection(string Format, int Version, ExecutionTargetBinding Binding,
    string RequestDigest, BlobReference Observation, OwnedTargetInventory Inventory)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public BlobReference? Retention { get; init; }
}
internal sealed record AndroidControlPlan(string Format, int Version, Guid InstanceId, Guid ParentJournalId,
    Guid ParentOperationId, Guid AttemptId, string StepId, string TotalPlanSha256, AndroidControlLimits Limits);
