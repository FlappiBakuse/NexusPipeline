using NexusPipeline.Shared.Results;

namespace NexusPipeline.Modules.Users.Contracts;

internal sealed record UserProviderConfigurationBinding(string UserId, string ScriptId, string Revision, string Fingerprint);

internal interface IUserProviderConfigurationStore
{
    UserProviderConfigurationBinding? FindProviderBinding(string userId, string scriptId);
    IReadOnlyList<UserProviderConfigurationBinding> ProviderBindings();
    OperationResult<bool> CommitProviderBinding(string userId, string scriptId, string expectedFingerprint,
        string revision, string expectedScriptFingerprint);
}
