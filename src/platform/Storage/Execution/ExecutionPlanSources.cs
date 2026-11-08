using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionPlanSources
{
    internal static void Validate(ExecutionPlan plan, AutomationBundle bundle)
    {
        var initialize = ProjectInitializationPlan.IsInitialization(plan);
        var roots = Unique(bundle.Roots, item => item.Id);
        var scripts = Unique(bundle.Scripts, item => item.Id);
        var users = Unique(bundle.Users, item => item.Id);
        var bindings = Unique(bundle.Bindings.Select(binding => NexusPipeline.Platform.Storage.Automation.BindingPolicyCodec.Resolve(
            users.TryGetValue(binding.UserId, out var owner) ? owner : throw new IOException("execution_account_binding_unbound"), binding)), item => item.Id);
        var queues = Unique(bundle.Queues, item => item.Id);
        var physical = Unique(plan.RootBindings, item => item.RootId);
        var revisions = new HashSet<(string Kind, string Id)>();
        foreach (var revision in plan.SourceRevisions)
        {
            RequireCanonicalId(revision.EntityId);
            var exists = revision.EntityKind switch
            {
                "root" => roots.ContainsKey(revision.EntityId), "script" => scripts.ContainsKey(revision.EntityId),
                "user" => users.ContainsKey(revision.EntityId), "binding" => bindings.ContainsKey(revision.EntityId),
                "queue" => queues.ContainsKey(revision.EntityId), _ => false
            };
            if (!exists || !revisions.Add((revision.EntityKind, revision.EntityId))) throw new IOException("execution_source_revision_invalid");
        }
        foreach (var root in physical.Values)
            if (!roots.ContainsKey(root.RootId) || !revisions.Contains(("root", root.RootId))) throw new IOException("execution_root_unbound");
        foreach (var step in plan.Steps)
        {
            RequireCanonicalId(step.ScriptId);
            if (step.UserId is not null) { RequireCanonicalId(step.UserId); RequireCanonicalId(step.BindingId!); }
            if (!scripts.TryGetValue(step.ScriptId, out var script) || !revisions.Contains(("script", step.ScriptId)) ||
                !physical.ContainsKey(script.RootId)) throw new IOException("execution_script_unbound");
            script = NexusPipeline.Platform.Storage.Automation.BindingSourceCodec.Resolve(bundle, step);
            ValidateStep(plan, step, script, physical);
            if (step.UserId is not null && (!users.ContainsKey(step.UserId) || !bindings.TryGetValue(step.BindingId!, out var binding) ||
                !binding.Enabled || binding.ScriptId != step.ScriptId || binding.UserId != step.UserId ||
                !revisions.Contains(("user", step.UserId)) || !revisions.Contains(("binding", step.BindingId!))))
                throw new IOException("execution_account_binding_unbound");
            if (step.BindingId is not null && (step.MaximumSuccessfulRunsPerDay ?? -1) !=
                (initialize ? -1 : bindings[step.BindingId].MaximumSuccessfulRunsPerDay ?? -1))
                throw new IOException("execution_daily_limit_unbound");
            if (!initialize) ExecutionHookInputs.Require(step, step.BindingId is not null && bindings.TryGetValue(step.BindingId, out var hookBinding)
                ? hookBinding : null, physical);
            if ((step.TotalTimeoutSeconds ?? script.Limits.TotalTimeoutSeconds) != script.Limits.TotalTimeoutSeconds ||
                (step.MaximumAttempts ?? 1) != (initialize ? 1 : script.Limits.MaximumAttempts) || plan.RetryPolicy.MaximumAttempts > script.Limits.MaximumAttempts)
                throw new IOException("execution_budget_exceeds_source");
        }
        ExecutionCaptureInputs.Require(plan);
        ExecutionRetryAuthority.Validate(plan, bundle);
        ValidateSelection(plan, bindings, revisions);
        PlanConfigurationSlots.Validate(plan);
        var unlimited = plan.Steps.Any(step => scripts[step.ScriptId].Limits.TotalTimeoutSeconds == 0);
        if (unlimited ? plan.Budget.TotalTimeoutSeconds != 0 : plan.Budget.TotalTimeoutSeconds == 0 ||
            plan.Budget.TotalTimeoutSeconds > Math.Min(604800, plan.Steps.Sum(step => scripts[step.ScriptId].Limits.TotalTimeoutSeconds)))
            throw new IOException("execution_budget_exceeds_source");
        ValidateQueue(plan, queues, bindings, users, revisions);
        foreach (var step in plan.Steps)
        {
            var dependencies = plan.QueueId is null ? [] : (queues[plan.QueueId].SuccessDependencies ?? [])
                .SingleOrDefault(item => item.ScriptId == step.ScriptId)?.RequiredScriptIds ?? [];
            var expected = dependencies.SelectMany(id =>
            {
                var ordinal = plan.Selection.ToList().FindIndex(value => value.StepId == step.StepId);
                var prior = plan.Selection.Take(ordinal).Where(value => value.ScriptId == id).ToArray();
                if (prior.Length == 0 || prior.Any(value => value.Disposition != "runnable")) throw new IOException("execution_dependency_unbound");
                return prior.Select(value => plan.Steps.Single(item => item.StepId == value.StepId).StepId);
            }).ToArray();
            if (!step.DependsOn.SequenceEqual(expected)) throw new IOException("execution_dependency_unbound");
        }
    }

    private static void ValidateSelection(ExecutionPlan plan, Dictionary<string, AutomationRelationsBinding> bindings,
        HashSet<(string Kind, string Id)> revisions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < plan.Selection.Count; ordinal++)
        {
            var item = plan.Selection[ordinal];
            RequireCanonicalId(item.BindingId); RequireCanonicalId(item.ScriptId); RequireCanonicalId(item.UserId);
            if (!seen.Add(item.StepId) || item.StepId != "binding-" + item.BindingId + "-" +
                ordinal.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) || !bindings.TryGetValue(item.BindingId, out var binding) ||
                binding.ScriptId != item.ScriptId || binding.UserId != item.UserId ||
                !revisions.Contains(("binding", item.BindingId)) || !revisions.Contains(("script", item.ScriptId)) ||
                !revisions.Contains(("user", item.UserId)) || !binding.Enabled && item.Disposition != "skipped" ||
                (item.Disposition == "runnable") != (item.ReasonCode == "execution_inputs_verified"))
                throw new IOException("execution_selection_unbound");
        }
        var runnable = plan.Selection.Where(item => item.Disposition == "runnable").Select(item => (item.StepId, item.BindingId)).ToArray();
        if (runnable.Length == 0 || !plan.Steps.Where(step => step.BindingId is not null).Select(step => (step.StepId, step.BindingId!)).SequenceEqual(runnable))
            throw new IOException("execution_selection_steps_unbound");
    }

    private static void ValidateStep(ExecutionPlan plan, ExecutionPlanStep step, AutomationScriptScript script, Dictionary<string, ExecutionPlanRootBindingsItem> roots)
    {
        var initialize = step.Purpose == "initialization";
        if (!initialize) ExecutionTargetInputs.Require(step.Target, script.Target, roots);
        if (initialize || script.Judgement.Language == "keywords")
        {
            if (step.JudgementWorker is not null) throw new IOException("execution_judgement_worker_unbound");
        }
        else
        {
            var worker = step.JudgementWorker ?? throw new IOException("execution_judgement_worker_unbound");
            var brokerStem = Path.GetFileNameWithoutExtension(worker.Broker.Path);
            if (new[] { brokerStem + ".deps.json", brokerStem + ".runtimeconfig.json", brokerStem + ".dll" }
                .Any(required => !worker.Broker.RuntimeFiles.Any(file => file.RelativePath == required)))
                throw new IOException("execution_judgement_broker_assets_missing");
            if ((script.Judgement.Language == "python") != (worker.Python is not null) || script.Judgement.Script is null ||
                worker.ScriptSha256 != NexusPipeline.Platform.Execution.Judgement.JudgementWorkerProtocol.Hash(
                    NexusPipeline.Platform.Execution.Judgement.JudgementWorkerProtocol.Utf8.GetBytes(script.Judgement.Script)))
                throw new IOException("execution_judgement_source_unbound");
            foreach (var image in new[] { worker.Broker, worker.Python }.OfType<ExecutionPlanInterpreterImage>())
            {
                if (PhysicalPathPolicy.NormalizeSyntax(image.Path) != image.Path) throw new IOException("execution_judgement_path_not_canonical");
                if (image.RuntimeFiles.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != image.RuntimeFiles.Count)
                    throw new IOException("execution_judgement_runtime_assets_invalid");
                string? prior = null;
                foreach (var file in image.RuntimeFiles)
                {
                    PhysicalPathPolicy.ValidateRelativePath(file.RelativePath);
                    if (prior is not null && StringComparer.Ordinal.Compare(prior, file.RelativePath) >= 0 ||
                        string.Equals(file.RelativePath, Path.GetFileName(image.Path), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("execution_judgement_runtime_assets_invalid");
                    prior = file.RelativePath;
                }
            }
        }
        if (step.UpdateConfigurationAtFinish != (!initialize && script.UpdateConfigurationAtFinish &&
            plan.Steps.Last(item => item.BindingId == step.BindingId).StepId == step.StepId) || !MatchesScriptLaunch(step, Resolve(script.Executable, roots), script.Arguments) ||
            step.WorkingDirectory != roots[script.RootId].Path ||
            !step.ConfigurationTargets.SequenceEqual(script.ConfigurationPaths.Select(path => Resolve(path, roots))) ||
            (script.Kind == "generic" ? step.EngineKind != "process" || step.Adapter is not null : step.ExtensionId != script.ExtensionId ||
                (script.AdapterResources.Count == 0 ? step.EngineKind != "extensionWorker" :
                    step.EngineKind is not ("process" or "extensionWorker") || step.Adapter is null)))
            throw new IOException("execution_launch_target_unbound");
        if (step.LogTargets.Count != (initialize ? 0 : script.LogPaths.Count)) throw new IOException("execution_log_selector_unbound");
        foreach (var (path, index) in script.LogPaths.Select((path, index) => (path, index)).Where(_ => !initialize))
            ExecutionLogSelectors.Require(step.LogTargets[index], index, ExecutionLogSelectors.Declaration(roots[path.RootId].Path, path.Relative));
        if (step.ConfigurationTargets.Any(path => step.LogTargets.Any(log => StringComparer.OrdinalIgnoreCase.Equals(path, log.ScopePath))))
            throw new IOException("execution_overlapping_target_roles");
    }

    internal static bool MatchesScriptLaunch(ExecutionPlanStep step, string path, IReadOnlyList<string> arguments) => step.EngineKind switch
    {
        "process" when step.Provider is null => step.ExecutablePath == path && step.Arguments.SequenceEqual(arguments),
        "extensionWorker" when step.Provider is not null => step.Provider.Upstream.Path == path && step.Provider.UpstreamArguments.SequenceEqual(arguments),
        _ => false
    };

    private static void ValidateQueue(ExecutionPlan plan, Dictionary<string, AutomationRelationsQueue> queues,
        Dictionary<string, AutomationRelationsBinding> bindings, Dictionary<string, AutomationRelationsUser> users,
        HashSet<(string Kind, string Id)> revisions)
    {
        if (plan.QueueId is null)
        {
            if (plan.CompletionAction != "none") throw new IOException("execution_completion_action_unbound");
            return;
        }
        if (!queues.TryGetValue(plan.QueueId, out var queue) || !revisions.Contains(("queue", plan.QueueId)) ||
            !plan.Selection.Select(item => item.BindingId).SequenceEqual(queue.ScriptIds.SelectMany(id => bindings.Values
                .Where(binding => binding.ScriptId == id && binding.Enabled).OrderBy(binding => users[binding.UserId].Order)
                .ThenBy(binding => binding.UserId, StringComparer.Ordinal).Select(binding => binding.Id))) ||
            plan.CompletionAction != (queue.CompletionAction == "exit" ? "exitHost" : queue.CompletionAction))
            throw new IOException("execution_queue_unbound");
    }

    private static string Resolve(AutomationPathsPortablePath path, Dictionary<string, ExecutionPlanRootBindingsItem> roots)
    {
        if (!roots.TryGetValue(path.RootId, out var root)) throw new IOException("execution_portable_root_missing");
        PhysicalPathPolicy.ValidateRelativePath(path.Relative);
        return PhysicalPathPolicy.NormalizeSyntax(Path.Combine(root.Path, path.Relative));
    }

    private static Dictionary<string, T> Unique<T>(IEnumerable<T> values, Func<T, string> key)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            RequireCanonicalId(key(item));
            if (!result.TryAdd(key(item), item)) throw new IOException("execution_source_identity_duplicated");
        }
        return result;
    }

    internal static void RequireCanonicalId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || id.ToString("D") != value)
            throw new IOException("execution_source_identity_not_canonical");
    }
}
