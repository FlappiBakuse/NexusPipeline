using NexusPipeline.Modules.Settings;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Security;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>Host 对 Settings 的唯一代理投影；Platform 不依赖完整 AppSettings。</summary>
internal static class OutboundProxyOptionsAdapter
{
    public static OutboundProxyOptions FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? decryptedPassword = null;
        if (!string.IsNullOrWhiteSpace(settings.ProxyPassword)
            && !SecretStore.TryDecrypt(settings.ProxyPassword, out decryptedPassword))
        {
            throw new InvalidDataException("代理密码无法解密，请重新填写代理密码");
        }

        return new OutboundProxyOptions(
            settings.ProxyMode ?? "none",
            settings.ProxyUrl ?? "",
            settings.ProxyUsername ?? "",
            decryptedPassword ?? "");
    }
}
