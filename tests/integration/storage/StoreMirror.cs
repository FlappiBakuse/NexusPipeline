using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NexusPipeline.StorageTests;

internal sealed class StoreMirror : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly int _port;
    internal ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    internal ConcurrentQueue<string> Requests { get; } = new();
    internal bool Offline { get; set; }

    private StoreMirror(WebApplication application, int port)
    { _application = application; _port = port; }

    internal static async Task<StoreMirror> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var application = builder.Build();
        StoreMirror? mirror = null;
        application.Run(async context =>
        {
            var uri = context.Request.Query["source"].ToString();
            mirror!.Requests.Enqueue(uri);
            if (mirror.Offline || !mirror.Files.TryGetValue(uri, out var bytes)) { context.Response.StatusCode = 404; return; }
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        });
        try
        {
            await application.StartAsync();
            var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            mirror = new(application, new Uri(address).Port);
            return mirror;
        }
        catch { await application.DisposeAsync(); throw; }
    }

    internal HttpClient Client() => new(new Routing(_port)) { Timeout = TimeSpan.FromSeconds(30) };

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync(); await _application.DisposeAsync();
    }

    private sealed class Routing(int port) : HttpMessageHandler
    {
        private readonly HttpClient _client = new(new SocketsHttpHandler
            { UseProxy = false, AllowAutoRedirect = false, UseCookies = false });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri is not { Scheme: "https" } source ||
                source.Host is not ("store.example.test" or "assets.example.test")) throw new IOException("mirror_source_rejected");
            var address = $"http://127.0.0.1:{port}/?source={Uri.EscapeDataString(source.AbsoluteUri)}";
            var response = await _client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.RequestMessage = request;
            return response;
        }

        protected override void Dispose(bool disposing)
        { if (disposing) _client.Dispose(); base.Dispose(disposing); }
    }
}
