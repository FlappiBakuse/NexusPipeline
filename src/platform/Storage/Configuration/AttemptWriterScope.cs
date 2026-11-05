using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class AttemptWriterScope : IConfigurationWriterScope
{
    private readonly IConfigurationWriterScope _parent;
    private readonly RunWriterScope _configuration;
    private bool _parentRegistered, _configurationRegistered;
    public Guid OperationId => _configuration.OperationId;
    public Guid ProcessScopeId => _configuration.ProcessScopeId;
    public ImmutableArray<Guid> JournalIds => _configuration.JournalIds;
    public ConfigurationEditorCommand Command => _configuration.Command;
    public CommandLineSyntax CommandLineSyntax => _parent.CommandLineSyntax;
    internal Guid ParentJournalId => _parent.JournalIds[0];

    internal AttemptWriterScope(IConfigurationWriterScope parent, RunWriterScope configuration)
    {
        if (configuration.OwnerOperationId != parent.OperationId || parent.ProcessScopeId != configuration.ProcessScopeId ||
            parent.JournalIds.Length != 1 || configuration.JournalIds.Contains(parent.JournalIds[0]) || parent.Command is null ||
            parent.CommandLineSyntax != configuration.CommandLineSyntax || !EditorCommandPolicy.Equal(parent.Command, configuration.Command)) throw new IOException("attempt_configuration_writer_unbound");
        _parent = parent; _configuration = configuration;
    }

    internal StepWriterScope RequireUnregisteredParent()
    {
        if (_parentRegistered || _configurationRegistered || _parent is not StepWriterScope scope)
            throw new IOException("execution_unregistered_job_release_unverified");
        return scope;
    }

    internal StepWriterScope RequireProviderParent()
    {
        if (!_parentRegistered || !_configurationRegistered || _parent is not StepWriterScope { Role: "process" } scope)
            throw new IOException("execution_provider_admission_closed");
        return scope;
    }

    internal StepWriterScope RequireCaptureParent()
    {
        if (!_parentRegistered || !_configurationRegistered || _parent is not StepWriterScope { Role: "process" } scope)
            throw new IOException("execution_capture_admission_closed");
        return scope;
    }

    public void RequirePrepared() { _parent.RequirePrepared(); _configuration.RequirePrepared(); }
    public void RequireStart()
    {
        if (!_parentRegistered || !_configurationRegistered) throw new IOException("configuration_writer_registration_incomplete");
        _parent.RequireStart(); _configuration.RequireStart();
    }
    public void RegisterJob(OwnedWorkerJob job)
    {
        if (!_parentRegistered) { _parent.RegisterJob(job); _parentRegistered = true; }
        if (!_configurationRegistered) { _configuration.RegisterJob(job); _configurationRegistered = true; }
    }
    public void RecordRoot(string jobName, ProcessIdentity root)
    { _parent.RecordRoot(jobName, root); _configuration.RecordRoot(jobName, root); }
    public void CloseAdmission(long elapsedMilliseconds)
    {
        if (!_parentRegistered) throw new IOException("execution_writer_job_unbound");
        _parent.CloseAdmission(elapsedMilliseconds);
        if (_configurationRegistered) _configuration.CloseAdmission(elapsedMilliseconds);
    }
    public FrozenJobClosure BindClosure(FrozenJobClosure closure, long elapsedMilliseconds) => _configurationRegistered
        ? _configuration.BindClosure(closure, elapsedMilliseconds)
        : _parent.BindClosure(closure with { OperationId = _parent.OperationId, JournalIds = _parent.JournalIds }, elapsedMilliseconds);

    public BlobReference RecordClosed(FrozenJobClosure closure)
    {
        if (!_configurationRegistered) return _parent.RecordClosed(closure);
        var reference = _configuration.RecordClosed(closure);
        var parentClosure = _parent.BindClosure(closure with { OperationId = _parent.OperationId, JournalIds = _parent.JournalIds },
            closure.RunElapsedMilliseconds ?? throw new IOException("attempt_configuration_elapsed_unbound"));
        _parent.RecordClosed(parentClosure);
        return reference;
    }
}
