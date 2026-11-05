using System.Net;
using System.Security;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Settings;

namespace NexusPipeline.Platform.Networking;

internal static class ProbeHttpClient
{
    internal static HttpClient Create(ProxyPreferences proxy, SecretValue? password) => Create(proxy, password, store: false);
    internal static HttpClient CreateStore(ProxyPreferences proxy, SecretValue? password) => Create(proxy, password, store: true);

    private static HttpClient Create(ProxyPreferences proxy, SecretValue? password, bool store)
    {
        if (!Enum.IsDefined(proxy.Mode) || password is not null && proxy.Mode != ProxyMode.Http)
            throw new IOException("http_proxy_configuration_invalid");
        var handler = new ProxyHandler
        {
            AllowAutoRedirect = !store, MaxAutomaticRedirections = 50, UseCookies = !store,
            AutomaticDecompression = store ? DecompressionMethods.None : DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseDefaultCredentials = false, PreAuthenticate = false,
            UseProxy = proxy.Mode != ProxyMode.None
        };
        try
        {
            if (proxy.Mode == ProxyMode.Http)
            {
                if (!Uri.TryCreate(proxy.Url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") ||
                    address.UserInfo.Length != 0 || address.Fragment.Length != 0)
                    throw new IOException("http_proxy_configuration_invalid");
                var configured = new WebProxy(address) { BypassProxyOnLocal = true };
                if (!string.IsNullOrWhiteSpace(proxy.UserName))
                {
                    using var secure = new SecureString();
                    password?.Use(value => { foreach (var character in value) secure.AppendChar(character); });
                    secure.MakeReadOnly();
                    handler.Credential = new NetworkCredential(proxy.UserName, secure);
                    configured.Credentials = handler.Credential;
                }
                handler.Proxy = new LoopbackBypassProxy(configured);
            }
            else if (proxy.Mode == ProxyMode.System) handler.Proxy = new LoopbackBypassProxy(HttpClient.DefaultProxy);
            return new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }
        catch { handler.Dispose(); throw; }
    }

    private sealed class LoopbackBypassProxy(IWebProxy source) : IWebProxy
    {
        public ICredentials? Credentials { get => source.Credentials; set => source.Credentials = value; }
        public Uri? GetProxy(Uri destination) => destination.IsLoopback ? destination : source.GetProxy(destination);
        public bool IsBypassed(Uri destination) => destination.IsLoopback || source.IsBypassed(destination);
    }

    private sealed class ProxyHandler : HttpClientHandler
    {
        internal NetworkCredential? Credential { get; set; }
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally
            {
                if (disposing && Credential is { } credential)
                {
                    credential.Password = string.Empty;
                    Credential = null;
                }
            }
        }
    }
}
