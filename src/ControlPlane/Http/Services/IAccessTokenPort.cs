namespace NexusPipeline.ControlPlane.Http.Services;

/// <summary>Control-plane port for decrypting a configured access token.</summary>
internal interface IAccessTokenPort
{
    bool TryDecrypt(string stored, out string? plaintext);
}
