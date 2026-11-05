using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class EditWriterScope : IConfigurationWriterScope
{
    private readonly ConfigurationEditStore _edits;
    private readonly JobClosureStore _closures;
    private readonly WriterAdmissionStore _admission;
    private readonly JournalWriterStore _writers;
    private readonly string _subject;
    public Guid OperationId { get; }
    public Guid ProcessScopeId => OperationId;
    public ImmutableArray<Guid> JournalIds { get; }
    public ConfigurationEditorCommand? Command { get; }

    internal EditWriterScope(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid sessionId, string subject)
    {
        OperationId = sessionId; _subject = subject;
        _edits = new(instance, database, contracts); _closures = new(instance, database, contracts);
        _admission = new(instance, database, contracts); _writers = new(database);
        var session = Required();
        ConfigurationEditStore.RequireOpen(session, 1, ConfigurationEditPhase.Prepared);
        _admission.RequireEditorAdmission(sessionId);
        var plans = new ConfigurationPlanCodec(instance, contracts, new(instance, contracts));
        foreach (var resource in session.Resources)
        {
            var path = Path.Combine(instance.RuntimeRoot, resource.PlanPath);
            var plan = plans.Read(new(resource.PlanPath, new FileInfo(path).Length, resource.PlanDigest));
            if (plan.EditPolicy is null) throw new IOException("configuration_editor_write_policy_required");
            if (resource == session.Resources[0]) Command = plan.Editor;
            else if ((Command is null) != (plan.Editor is null) || Command is not null && !EditorCommandPolicy.Equal(Command, plan.Editor!))
                throw new IOException("configuration_editor_command_unbound");
        }
        JournalIds = session.Resources.Select(resource => resource.JournalId).OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToImmutableArray();
    }

    public void RequirePrepared()
    {
        ConfigurationEditStore.RequireOpen(Required(), 1, ConfigurationEditPhase.Prepared);
        _admission.RequireEditorAdmission(OperationId);
    }
    public void RequireStart()
    {
        ConfigurationEditStore.RequireOpen(Required(), 2, ConfigurationEditPhase.Editing);
        _admission.RequireEditorAdmission(OperationId);
    }
    public void RegisterJob(OwnedWorkerJob job) => _writers.RegisterJob(JournalIds, job);
    public void RecordRoot(string jobName, ProcessIdentity root) => _closures.RecordRoot(OperationId, JournalIds, jobName, root);
    public void CloseAdmission(long elapsedMilliseconds) => _admission.CloseEditorAdmission(OperationId);
    public FrozenJobClosure BindClosure(FrozenJobClosure closure, long elapsedMilliseconds) => closure;
    public BlobReference RecordClosed(FrozenJobClosure closure) => _closures.RecordClosed(closure);
    private ConfigurationEditStatus Required() => _edits.Read(OperationId, _subject) ?? throw new ConfigurationException("configuration_edit_missing");
}
