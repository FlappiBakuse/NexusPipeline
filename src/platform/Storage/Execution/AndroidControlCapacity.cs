using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

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
