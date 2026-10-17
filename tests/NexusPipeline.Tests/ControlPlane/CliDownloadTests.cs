using System.Net;
using System.Net.Sockets;
using System.Text;
using NexusPipeline.ControlPlane.Cli;
using Xunit;

namespace NexusPipeline.Tests.ControlPlane;

public sealed class CliDownloadTests
{
    [Fact]
    public async Task DownloadMapsTransportErrorsAndLeavesNoDestination()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-cli-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var listener = new HttpListener();
            int port = FreePort();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var pending = listener.GetContextAsync();
            var request = Task.Run(() => Client(port).Download("/timeout", Path.Combine(root, "timeout.bin")));
            var context = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            var timedOut = await request.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("timeout", timedOut.Code);
            Assert.Equal(408, timedOut.StatusCode);
            context.Response.Abort();
            listener.Stop();
            var disconnected = Client(port).Download("/offline", Path.Combine(root, "offline.bin"));
            Assert.Equal("service_unavailable", disconnected.Code);
            Assert.Equal(503, disconnected.StatusCode);
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DownloadPreservesErrorArgumentsNoContentAndCreateNewOutput()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-cli-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        try
        {
            async Task<CliApiResponse> Respond(int status, string body, string destination)
            {
                var pending = listener.GetContextAsync();
                var request = Task.Run(() => Client(port).Download("/artifact", destination));
                var context = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                context.Response.StatusCode = status;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentLength64 = bytes.Length;
                if (bytes.Length > 0) await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
                return await request.WaitAsync(TimeSpan.FromSeconds(10));
            }
            string output = Path.Combine(root, "artifact.bin");
            var error = await Respond(409, "{\"code\":\"execution_resource_in_use\",\"args\":{\"resource\":\"fixture\"}}", output);
            Assert.Equal("resource_busy", error.Code);
            Assert.Equal("fixture", error.Args!["resource"]!.GetValue<string>());
            var scalar = await Respond(409, "\"fixture-error\"", output);
            Assert.Equal("resource_busy", scalar.Code);
            Assert.Null(scalar.Args);
            var empty = await Respond(204, "", output);
            Assert.True(empty.Succeeded);
            Assert.False(File.Exists(output));
            var success = await Respond(200, "fixture-bytes", output);
            Assert.True(success.Succeeded);
            Assert.Equal("fixture-bytes", File.ReadAllText(output));
            var collision = Client(port).Download("/unused", output);
            Assert.Equal("validation_error", collision.Code);
            Assert.Equal("fixture-bytes", File.ReadAllText(output));
        }
        finally { listener.Stop(); Directory.Delete(root, true); }
    }

    private static CliApiClient Client(int port)
    {
        var client = new CliApiClient();
        typeof(CliApiClient).GetProperty(nameof(CliApiClient.Port))!.SetValue(client, port);
        return client;
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
