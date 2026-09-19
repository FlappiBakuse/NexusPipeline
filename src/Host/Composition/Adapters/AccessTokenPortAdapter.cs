using NexusPipeline.ControlPlane.Http.Services;
using NexusPipeline.Platform.Security;

namespace NexusPipeline.Host.Composition.Adapters;

internal sealed class AccessTokenPortAdapter : IAccessTokenPort
{
    public bool TryDecrypt(string stored, out string? plaintext)
        => SecretStore.TryDecrypt(stored, out plaintext);
}
