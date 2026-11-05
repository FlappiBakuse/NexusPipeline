using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Core.Extensions;

public static class AdapterSelectionPolicy
{
    public static AdapterSelection Freeze(AdapterSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection); ExtensionIdentifiers.Require(selection.ExtensionId);
        if (selection.OperationId == Guid.Empty || selection.BindingId == Guid.Empty ||
            selection.ExpectedBindingRevision is < 1 or > 9007199254740991 ||
            selection.ExpectedInstalledRevision is < 1 or > 9007199254740991 ||
            selection.Resources.IsDefaultOrEmpty || selection.Resources.Length > RunRequestPolicy.MaximumResources || selection.Resources.Any(resource => resource is null) ||
            selection.Resources.Select(resource => resource.ResourceId).Distinct(StringComparer.Ordinal).Count() != selection.Resources.Length)
            throw new ArgumentException("adapter_selection_invalid");
        foreach (var resource in selection.Resources)
            if (string.IsNullOrWhiteSpace(resource.ResourceId) || resource.ResourceId.Length > 120 || resource.ResourceId.Any(char.IsControl) ||
                resource.ExpectedRevision is < 1 or > 9007199254740991 || !FileTreePolicy.RelativePath(resource.RelativePath))
                throw new ArgumentException("adapter_resource_selection_invalid");
        var intents = selection.ConfigurationIntents.IsDefault ? [] : selection.ConfigurationIntents;
        if (intents.Length > 256 || intents.Any(item => item is null) ||
            intents.Select(item => item.Binding.ResourceId).Distinct(StringComparer.Ordinal).Count() != intents.Length)
            throw new ArgumentException("adapter_configuration_intents_invalid");
        var frozen = intents.Select(ConfigurationPatchPolicy.Freeze).ToImmutableArray();
        if (frozen.Any(item => item.Binding.OperationId != selection.OperationId ||
            !selection.Resources.Any(resource => resource.ResourceId == item.Binding.ResourceId && resource.ExpectedRevision == item.Binding.Revision)))
            throw new ArgumentException("adapter_configuration_intent_unbound");
        return selection with { Resources = selection.Resources.ToImmutableArray(), ConfigurationIntents = frozen };
    }

    public static FrozenAdapterAttempt Freeze(FrozenAdapterAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.OperationId == Guid.Empty || attempt.RunId == Guid.Empty || attempt.AttemptId == Guid.Empty ||
            string.IsNullOrWhiteSpace(attempt.StepId) || attempt.StepId.Length > 120 || attempt.StepId.Any(char.IsControl) ||
            !FileTreePolicy.Digest(attempt.PlanDigest)) throw new ArgumentException("adapter_frozen_attempt_invalid");
        return attempt;
    }

    public static FrozenAdapterRetry Freeze(FrozenAdapterRetry retry)
    {
        ArgumentNullException.ThrowIfNull(retry); _ = Freeze(retry.Attempt);
        if (string.IsNullOrWhiteSpace(retry.IdempotencyKey) || retry.IdempotencyKey.Length > 256 || retry.IdempotencyKey.Any(char.IsControl) ||
            retry.Observations.IsDefault || retry.Observations.Length > 4096 || retry.Observations.Any(observation => observation is null) ||
            retry.Observations.Select(observation => observation.TaskId).Distinct(StringComparer.Ordinal).Count() != retry.Observations.Length)
            throw new ArgumentException("adapter_retry_request_invalid");
        if (retry.Observations.Any(observation => !TaskIdentity(observation.TaskId) || !TaskIdentity(observation.ReasonCode) ||
            !Enum.IsDefined(observation.State) || !AdapterTaskPolicy.ValidCompletion(observation)))
            throw new ArgumentException("adapter_retry_observation_invalid");
        return retry with { Observations = retry.Observations.ToImmutableArray() };
    }
    private static bool TaskIdentity(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120 && !value.Any(char.IsControl);
}
