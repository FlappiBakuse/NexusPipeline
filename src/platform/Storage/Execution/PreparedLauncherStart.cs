using System.ComponentModel;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PreparedLauncherStart
{
    private readonly OwnedWorkerJob _job;
    private readonly OwnedWorkerIdentity? _root;
    private readonly int? _nativeError;
    internal ProviderLauncherBinding Binding { get; }
    internal bool Succeeded => _root is not null;

    private PreparedLauncherStart(OwnedWorkerJob job, ProviderLauncherBinding binding,
        OwnedWorkerIdentity? root, int? nativeError)
    { _job = job; Binding = binding; _root = root; _nativeError = nativeError; }

    internal static PreparedLauncherStart Start(ProviderLauncherStore store,
        ProviderLauncherBinding binding, OwnedWorkerJob job)
    {
        try
        {
            var root = store.Start(binding, job, command => job.StartFrozen(command.ExecutablePath, command.Arguments,
                command.WorkingDirectory, binding.InstanceId, binding.AttemptId,
                admitted => store.RecordRoot(binding, job, admitted), output: null, CommandLineSyntax.ArgumentVector));
            return new(job, binding, root, null);
        }
        catch (Win32Exception error) { return new(job, binding, null, error.NativeErrorCode); }
    }

    internal T Use<T>(ProviderLauncherBinding expected, Func<ProcessIdentity?, int?, T> callback)
    {
        if (expected != Binding || _root is null && _nativeError is null) throw new IOException("execution_provider_launcher_start_unbound");
        if (_root is null) return callback(null, _nativeError);
        _ = _job.ReadExitCode(_root);
        return callback(new(_root.ProcessId, _root.CreatedAtUtc, _root.ImagePath, _root.ImageSha256, _root.UserSid, _root.IsElevated), null);
    }
}
