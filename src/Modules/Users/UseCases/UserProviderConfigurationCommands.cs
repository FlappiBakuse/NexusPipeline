using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users.Persistence;
using NexusPipeline.Shared.Results;

namespace NexusPipeline.Modules.Users.UseCases;

internal sealed partial class UserCommands
{
    public UserProviderConfigurationBinding? FindProviderBinding(string userId, string scriptId)
    {
        var user = _state.Find(userId);
        var binding = user?.Bindings.FirstOrDefault(item => item.ScriptInstanceId == scriptId);
        return user is null || binding is null ? null : new(user.Id, scriptId,
            binding.ExecutionProviderConfigRevision, BindingFingerprint(binding));
    }

    public IReadOnlyList<UserProviderConfigurationBinding> ProviderBindings() => _state.Snapshot()
        .SelectMany(user => user.Bindings.Where(binding => binding.ExecutionProviderConfigRevision.Length != 0)
            .Select(binding => new UserProviderConfigurationBinding(user.Id, binding.ScriptInstanceId,
                binding.ExecutionProviderConfigRevision, BindingFingerprint(binding)))).ToArray();

    public OperationResult<bool> CommitProviderBinding(string userId, string scriptId, string expectedFingerprint,
        string revision, string expectedScriptFingerprint)
    {
        if (!ProviderConfigurationContract.SafeId(revision)) return Validation<bool>("configuration.revision.invalid");
        try
        {
            OperationResult<bool> result = Conflict<bool>("configuration.user.cas_conflict", "User binding changed during initialization");
            _admission.WithCoordination(() => _state.Mutate(state => {
                var user = state.Users.FirstOrDefault(item => item.Id == userId);
                var binding = user?.Bindings.FirstOrDefault(item => item.ScriptInstanceId == scriptId);
                if (binding is null || BindingFingerprint(binding) != expectedFingerprint
                    || ProviderConfigurationContract.Fingerprint(_scripts.FindById(scriptId)) != expectedScriptFingerprint) return;
                string previous = binding.ExecutionProviderConfigRevision;
                binding.ExecutionProviderConfigRevision = revision;
                try { UserDefinitionStore.SaveUsers(state.Users); }
                catch { binding.ExecutionProviderConfigRevision = previous; throw; }
                result = OperationResult<bool>.Ok(true);
            }));
            if (result.Succeeded) _plansChanged.RevalidatePendingPlans();
            return result;
        }
        catch (Exception error) { return Internal<bool>(error); }
    }
}
