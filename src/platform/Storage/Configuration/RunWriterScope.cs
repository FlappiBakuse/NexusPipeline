using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class RunWriterScope : IConfigurationWriterScope
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly ConfigurationRunStore _runs;
    private readonly JobClosureStore _closures;
    private readonly WriterAdmissionStore _admission;
    private readonly JournalWriterStore _writers;
    private readonly string _subject;
    private string? _jobName;
    private readonly IConfigurationWriterScope? _parentScope;
    internal bool IsHookGroup { get; }
    internal string Role { get; }
    public Guid OperationId { get; }
    public Guid ProcessScopeId { get; }
    internal Guid OwnerOperationId { get; }
    public ImmutableArray<Guid> JournalIds { get; }
    public ConfigurationEditorCommand Command { get; }
    public CommandLineSyntax CommandLineSyntax { get; }
    internal bool IsJobRegistered => _jobName is not null;

    internal RunWriterScope(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid sessionId, string subject,
        IConfigurationWriterScope? parentScope = null)
    {
        _instance = instance; _database = database; _contracts = contracts; _parentScope = parentScope;
        OperationId = sessionId; _subject = subject; _runs = new(instance, database, contracts); _closures = new(instance, database, contracts);
        _admission = new(instance, database, contracts); _writers = new(database);
        var session = Required();
        OwnerOperationId = session.OwnerOperationId;
        var binding = _runs.ReadBound(session);
        ProcessScopeId = Guid.Parse(binding.Evidence.AttemptId);
        JournalIds = session.Resources.Select(item => item.JournalId).OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToImmutableArray();
        IsHookGroup = binding.Evidence.HasHooks;
        Role = parentScope is StepWriterScope execution ? execution.Role : "process";
        if (IsHookGroup && (parentScope is not StepWriterScope || Role == "judgement"))
            throw new IOException("configuration_hooks_parent_required");
        RequireGroupAdmission(session);
        (Command, CommandLineSyntax) = binding.Evidence.WriterCommand(Role);
        RequireParentAdmission(session);
        _admission.RequireRunAdmission(sessionId);
    }

    public void RequirePrepared()
    { var session = Required(); RequireGroupAdmission(session); RequireParentAdmission(session); _admission.RequireRunAdmission(OperationId); }
    internal void RequireTargetInstallation()
    {
        var session = Required();
        if (_parentScope is not StepWriterScope parent || parent.Role != "process" || Role != "process" || _jobName is null)
            throw new IOException("configuration_target_parent_required");
        RequireParentAdmission(session);
        var state = AttemptWriterState.Read(_instance, _database, _contracts, session.ParentJournalId);
        if (state.Journal.Phase != JournalPhase.Applying || state.Gate.State != EffectGateState.Entered || state.IsDispatchClosed ||
            state.Latest.Revision != state.Journal.Revision || !(state.Total.Steps.Single(step => step.StepId == session.StepId).Target?.Kind is "pc" or "android" ||
                state.Total.Steps.Single(step => step.StepId == session.StepId).Provider?.Launcher is not null) ||
            !state.Jobs.Any(job => job.StepId == session.StepId && job.Role == "process" && job.JobName == _jobName && job.ClosedElapsedMilliseconds is null))
            throw new IOException("configuration_target_install_closed");
        var binding = StepResultCodec.Binding(state.Latest.Root, state.Jobs.Single(job => job.JobName == _jobName));
        if (new StepEvidenceReader(_instance, _database, _contracts).Context(binding).Root is not null)
            throw new IOException("configuration_target_install_after_main");
    }

    public void RequireStart()
    {
        var session = Required();
        if (session.Phase != ConfigurationRunPhase.Running || (!IsHookGroup && session.Revision != 2) || session.IsWriterAdmissionClosed ||
            !ParentAllowsStart(session) || Role == "process" && session.PrimaryJobName != _jobName) throw new IOException("configuration_writer_start_closed");
        _admission.RequireRunAdmission(OperationId);
    }
    public void RegisterJob(OwnedWorkerJob job)
    {
        if (_jobName is not null) throw new IOException("configuration_writer_job_already_bound");
        var session = Required();
        var parent = _parentScope is null ? null : AttemptWriterState.Read(_instance, _database, _contracts, session.ParentJournalId);
        _writers.RegisterJob(JournalIds, job, parent, session.StepId); _jobName = job.Name;
    }

    private void RequireParentAdmission(ConfigurationRunStatus session)
    {
        var binding = _runs.ReadBound(session);
        if (binding.ParentJournal.Phase == JournalPhase.Prepared) return;
        if (binding.ParentJournal.Phase != JournalPhase.Applying || _parentScope is null ||
            _parentScope.OperationId != OwnerOperationId || _parentScope.ProcessScopeId != ProcessScopeId ||
            !_parentScope.JournalIds.SequenceEqual(new[] { session.ParentJournalId }) || _parentScope.Command is null ||
            !EditorCommandPolicy.Equal(_parentScope.Command, Command)) throw new IOException("configuration_run_parent_admission_closed");
        _parentScope.RequirePrepared();
    }

    private bool ParentAllowsStart(ConfigurationRunStatus session)
    {
        var binding = _runs.ReadBound(session);
        if (binding.ParentJournal.Phase == JournalPhase.Prepared) return true;
        if (binding.ParentJournal.Phase != JournalPhase.Applying) return false;
        var state = AttemptWriterState.Read(_instance, _database, _contracts, session.ParentJournalId);
        return !state.IsDispatchClosed && state.Gate.State == EffectGateState.Entered && state.Latest.Revision == state.Journal.Revision &&
            state.Jobs.Any(step => step.StepId == session.StepId && step.Role == Role && step.JobName == _jobName && step.ClosedElapsedMilliseconds is null);
    }
    internal void RequireSampling(ConfigurationRunStatus expected, bool sealedOutput)
    {
        var current = Required();
        if (!sealedOutput && (Role != "process" || current.PrimaryJobName != _jobName)) throw new IOException("configuration_initial_primary_not_started");
        if (current.SessionId != expected.SessionId || current.OwnerOperationId != expected.OwnerOperationId || current.ParentJournalId != expected.ParentJournalId ||
            current.StepId != expected.StepId || current.Revision != expected.Revision || current.SyncOrdinal != expected.SyncOrdinal ||
            current.ExpectedBindingRevision != expected.ExpectedBindingRevision || current.Phase != expected.Phase ||
            current.PrimaryJobName != expected.PrimaryJobName ||
            current.ClosedElapsedMilliseconds != expected.ClosedElapsedMilliseconds ||
            current.IsWriterAdmissionClosed != expected.IsWriterAdmissionClosed || !current.Resources.SequenceEqual(expected.Resources) ||
            (sealedOutput ? current.Phase is not (ConfigurationRunPhase.Prepared or ConfigurationRunPhase.Running) : current.Phase != ConfigurationRunPhase.Running) ||
            current.IsWriterAdmissionClosed != sealedOutput)
            throw new IOException("configuration_run_sampling_closed");
    }
    public void RecordRoot(string jobName, ProcessIdentity root)
    {
        var session = Required();
        _closures.RecordRunRoot(OperationId, JournalIds, jobName, root, IsHookGroup ?
            AttemptWriterState.Read(_instance, _database, _contracts, session.ParentJournalId) : null);
    }
    public void CloseAdmission(long elapsedMilliseconds)
    {
        if (!IsHookGroup) { _admission.CloseRunAdmission(OperationId, elapsedMilliseconds); return; }
        var session = Required();
        var state = AttemptWriterState.Read(_instance, _database, _contracts, session.ParentJournalId);
        var job = state.Jobs.SingleOrDefault(job => job.JobName == _jobName && job.StepId == session.StepId && job.Role == Role);
        if (job?.ClosedElapsedMilliseconds != elapsedMilliseconds) throw new IOException("configuration_group_job_close_unbound");
    }
    public FrozenJobClosure BindClosure(FrozenJobClosure closure, long elapsedMilliseconds) =>
        closure with { RunElapsedMilliseconds = elapsedMilliseconds };
    public BlobReference RecordClosed(FrozenJobClosure closure) => _closures.RecordClosed(closure);
    private void RequireGroupAdmission(ConfigurationRunStatus session)
    {
        if (!IsHookGroup) { ConfigurationRunStore.RequirePrepared(session, 1); return; }
        if (session.Phase is not (ConfigurationRunPhase.Prepared or ConfigurationRunPhase.Running) || session.IsWriterAdmissionClosed ||
            session.ClosedElapsedMilliseconds.HasValue) throw new IOException("configuration_writer_registration_closed");
    }
    private ConfigurationRunStatus Required() => _runs.Read(OperationId, _subject) ?? throw new ConfigurationException("configuration_run_missing");
}
