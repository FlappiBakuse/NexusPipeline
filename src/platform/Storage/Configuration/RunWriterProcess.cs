using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
public sealed class RunWriterProcess : IDisposable
{
    private readonly ConfigurationWriterProcess _writer;
    private readonly RunWriterScope _scope;
    public RunWriterProcess(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease, Guid sessionId, string subject)
        : this(instance, database, contracts, lease, sessionId, subject, null) { }

    internal RunWriterProcess(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        Guid sessionId, string subject, IConfigurationWriterScope? parentScope, bool deferRegistration = false)
    {
        lease.VerifyHeld(instance);
        _scope = new(instance, database, contracts, sessionId, subject, parentScope);
        _writer = new(instance, lease, parentScope is null ? _scope : new AttemptWriterScope(parentScope, _scope), deferRegistration: deferRegistration);
    }
    internal string JobName => _writer.JobName;
    internal bool IsHookGroup => _scope.IsHookGroup;
    internal string Role => _scope.Role;
    internal bool HasConfigurationJobRegistration => _scope.IsJobRegistered;
    internal void RegisterPreparedJob() => _writer.RegisterPreparedJob();
    internal void ReleaseEmptyJob() => _writer.ReleaseEmptyJob();
    internal void RequireRegisteredJob() => _writer.RequireRegisteredJob();
    internal void RequireTargetInstallation() => _scope.RequireTargetInstallation();
    internal void RequireStoppedUnregistered(StoppedConfigurationEditor stopped) => _writer.RequireStoppedUnregistered(stopped);
    public OwnedWorkerIdentity Start() => _writer.Start();
    public OwnedWorkerIdentity Start(OwnedProcessOutput output) => _writer.Start(output);
    public Task<int?> AwaitRootAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        _writer.AwaitRootAsync(timeout, cancellationToken);
    internal Task<int?> WaitForWorkAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        _writer.WaitForWorkAsync(timeout, cancellationToken);
    internal TimeSpan ElapsedSinceStart => _writer.ElapsedSinceStart;
    internal void RequireScope(RuntimeInstance instance, Guid sessionId) => _writer.RequireScope(instance, sessionId);
    internal void RequireSampling(ConfigurationRunStatus session, bool sealedOutput) => _scope.RequireSampling(session, sealedOutput);
    public Task<StoppedConfigurationEditor> StopAsync(TimeSpan timeout) => _writer.StopAsync(timeout);
    internal Task AdmitProviderBootstrap(StepEvidenceBinding binding, OwnedProcessOutput output,
        Func<CancellationToken, Task> write, TimeSpan timeout) => _writer.AdmitProviderBootstrap(binding, output, write, timeout);

    internal T UsePrimaryParent<T>(StepEvidenceBinding binding, Func<OwnedWorkerJob, OwnedWorkerIdentity, T> start) =>
        _writer.UsePrimaryParent(binding, start);

    internal T UseStepEvidence<T>(StepEvidenceBinding binding, StoppedConfigurationEditor? stopped,
        Func<ProcessIdentity?, int?, T> callback) => _writer.UseStepEvidence(binding, stopped, callback);
    public void Dispose() => _writer.Dispose();
}
