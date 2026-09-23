using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Configuration.Validation;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Queues.Queries;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Shared.Localization;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>
/// Composition-layer adapter that joins Configuration-owned snapshot capture to
/// Execution-owned discovery and admission facts without creating a module cycle.
/// </summary>
internal sealed class TaskProtocolConfigAssessmentAdapter(
    QueueQueries queues) : ITaskProtocolConfigAssessmentPort
{
    public async Task<TaskPlan?> RunAsync(
        ResolvedScriptSpec spec,
        ResolvedScriptUser user,
        string trigger,
        CancellationToken token = default)
    {
        TaskProtocolDescriptor? protocol = spec.TaskProtocol;
        if (protocol is null)
        {
            return null;
        }

        string scriptId = spec.Script.Id;
        string userId = user.UserId;
        ConfigStoreMetadata? metadata = ConfigStoreMetadata.Load(scriptId, userId);
        if (metadata is null
            || metadata.ConfigLocatorHash != ConfigStoreMetadata.HashLocator(spec.Script.ConfigPath)
            || metadata.ConfigKind is not ("file" or "dir"))
        {
            throw new InvalidDataException("config_unavailable: snapshot metadata does not match the resolved configuration");
        }

        string store = ConfigPaths.StoreDir(scriptId, userId);
        if (!Directory.Exists(store))
        {
            throw new InvalidDataException("config_unavailable: user configuration snapshot is missing");
        }

        string config = metadata.ConfigKind == "file"
            ? Path.Combine(store, Path.GetFileName(spec.Script.ConfigPath))
            : store;
        string[] extras = spec.ExtraConfigPaths.Select(path =>
        {
            string saved = ConfigPaths.StoreExtraDir(scriptId, userId, path);
            string file = Path.Combine(saved, Path.GetFileName(path));
            return File.Exists(file) ? file : saved;
        }).ToArray();

        for (int attempt = 0; ; attempt++)
        {
            TaskConfigView view = TaskConfigViewFactory.Capture(
                config,
                spec.Script.RootPath,
                extras,
                protocol.ReadResources,
                spec.Script.ConfigPath,
                spec.ExtraConfigPaths);
            TaskQueueContextFact queueContext = TaskQueueContextResolver.Resolve(
                queues.List().Select(item => item.Queue).ToList(),
                scriptId);
            TaskExecutionContext context = ExecutionCoordinator.CreateTaskExecutionContext(
                spec.Script,
                spec,
                userId,
                trigger,
                queueContext.QueueId,
                queueContext.HasFollowingWork);
            try
            {
                return await TaskDiscoveryService.DiscoverAsync(
                    protocol,
                    view,
                    spec.Script.PluginType,
                    spec.PluginVersion,
                    userId,
                    scriptId,
                    LocaleContext.Current,
                    true,
                    token,
                    executionContext: context,
                    scriptRoot: spec.Script.RootPath,
                    scriptExecutable: spec.Script.MainExe).ConfigureAwait(false);
            }
            catch (InvalidDataException ex) when (attempt == 0
                && ex.Message.Contains("configuration_conflict", StringComparison.Ordinal))
            {
                // The view is immutable, so never combine bytes from two revisions;
                // discard this assessment and capture a fresh view once.
                token.ThrowIfCancellationRequested();
            }
        }
    }

    public IReadOnlyList<ConfigValidationDiagnostic> ToDiagnostics(TaskPlan plan, ResolvedScriptUser user)
    {
        if (plan.ConfigAssessment is not { } assessment)
        {
            return Array.Empty<ConfigValidationDiagnostic>();
        }

        string bindingKey = user.UserId + ":" + user.Binding.ScriptInstanceId;
        return assessment.Checks
            .Where(check => check.Evaluation is not ("satisfied" or "not_applicable"))
            .Select(check => new ConfigValidationDiagnostic(
                bindingKey,
                user.UserId,
                user.UserName,
                check.RuleId,
                check.Evaluation,
                check.Severity,
                check.ExecutionEffect,
                check.ReasonText?.DeepClone().AsObject(),
                check.Locations.DeepClone().AsArray(),
                check.Actions.DeepClone().AsArray()))
            .ToArray();
    }

    public ConfigValidationDiagnostic Error(
        ResolvedScriptSpec spec,
        ResolvedScriptUser user,
        string message)
    {
        return new ConfigValidationDiagnostic(
            user.UserId + ":" + spec.Script.Id,
            user.UserId,
            user.UserName,
            "task_protocol.assessment",
            "unknown",
            "warning",
            "warn",
            new JsonObject { ["kind"] = "literal", ["value"] = message },
            [],
            []);
    }
}
