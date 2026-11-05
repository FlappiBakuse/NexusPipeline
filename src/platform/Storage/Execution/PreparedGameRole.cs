using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record GameRoleFacts(ExecutionTargetBinding Binding, ConfigurationEditorCommand Launch,
    PcTargetImage ReadyImage, PcReadinessMode ReadinessMode, ExecutionTargetPolicy Policy, long WaitSeconds,
    CommandLineSyntax CommandLineSyntax);

[SupportedOSPlatform("windows")]
internal sealed class PreparedGameRole
{
    private readonly TargetJobStore _owner;
    private readonly OwnedWorkerJob _job;
    private readonly GameRoleFacts _facts;
    internal ExecutionTargetBinding Binding => _facts.Binding;

    private PreparedGameRole(TargetJobStore owner, OwnedWorkerJob job, GameRoleFacts facts)
    { _owner = owner; _job = job; _facts = facts; }

    internal static PreparedGameRole Read(TargetJobStore owner, ExecutionTargetBinding binding, OwnedWorkerJob actualJob)
    {
        if (actualJob.Name != binding.JobName) throw new IOException("execution_host_game_job_unbound");
        return new(owner, actualJob, owner.RoleFacts(binding));
    }

    internal T Use<T>(OwnedWorkerJob actualJob, ExecutionTargetBinding expected, Func<GameRoleFacts, T> callback) => _owner.Use(() =>
    {
        if (!ReferenceEquals(actualJob, _job) || expected != _facts.Binding) throw new IOException("execution_host_game_role_unbound");
        var current = _owner.RoleFacts(expected);
        if (!EditorCommandPolicy.Equal(current.Launch, _facts.Launch) || current.ReadyImage != _facts.ReadyImage ||
            current.Policy != _facts.Policy || current.ReadinessMode != _facts.ReadinessMode || current.WaitSeconds != _facts.WaitSeconds ||
            current.CommandLineSyntax != _facts.CommandLineSyntax) throw new IOException("execution_host_game_role_changed");
        return callback(_facts);
    });
}
