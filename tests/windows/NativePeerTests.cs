using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class NativePeerTests
{
    [Theory(DisplayName = "A Trusted Launch Registration Requires The Complete Observed Identity")]
    [InlineData("creation")]
    [InlineData("image")]
    [InlineData("path")]
    [InlineData("user")]
    [InlineData("elevation")]
    public void RequiresObservedIdentity(string mutation)
    {
        var observed = ProcessIdentityReader.Read(Environment.ProcessId);
        var expected = mutation switch
        {
            "creation" => observed with { CreatedAtUtc = observed.CreatedAtUtc.AddTicks(1) },
            "image" => observed with { ImageSha256 = new string('0', 64) },
            "path" => observed with { ImagePath = observed.ImagePath + ".unrelated" },
            "user" => observed with { UserSid = "S-1-0-0" },
            "elevation" => observed with { IsElevated = !observed.IsElevated },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<IOException>(() => { using var rejected = new NativeClientBinding(expected); });
    }

    [Fact(DisplayName = "Registered Native Ownership Ends With The Process Even While Its Kernel Identity Handle Is Retained")]
    public async Task RevokesExitedOwner()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "native-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var job = new OwnedWorkerJob();
        var identity = job.Start(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!,
            ["tcp-client", ((IPEndPoint)listener.LocalEndpoint).Port.ToString(), Path.Combine(root, "ready.json"), "1"], root,
            Guid.NewGuid(), Guid.NewGuid(), observed => File.WriteAllText(Path.Combine(root, "identity.json"), JsonSerializer.Serialize(observed)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
        var client = (IPEndPoint)connection.Client.RemoteEndPoint!;
        var server = (IPEndPoint)connection.Client.LocalEndPoint!;
        using var binding = new NativeClientBinding(identity.ProcessId);
        Assert.True(binding.Matches(client, server));
        using var canceled = new CancellationTokenSource();
        var canceledWait = binding.WaitForExitAsync(canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        Assert.True(binding.IsLive);
        var waits = Enumerable.Range(0, 8).Select(_ => binding.WaitForExitAsync(deadline.Token)).ToArray();
        Assert.All(waits, wait => Assert.False(wait.IsCompleted));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(5)));
        await Task.WhenAll(waits);
        await binding.WaitForExitAsync(deadline.Token);
        Assert.False(binding.Matches(client, server));
        var rejected = Record.Exception(() => { using var invalid = new NativeClientBinding(identity.ProcessId); });
        Assert.True(rejected is IOException or System.ComponentModel.Win32Exception);
        binding.Dispose();
        Assert.False(binding.Matches(client, server));
    }

    [Fact(DisplayName = "Actual Established Loopback Pair Resolves To Its Kernel Owner")]
    public async Task ResolvesEstablishedOwner()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await listener.AcceptTcpClientAsync();
        var owner = LoopbackTcpPeer.Observe((IPEndPoint)server.Client.RemoteEndPoint!, (IPEndPoint)server.Client.LocalEndPoint!);
        Assert.Equal(TcpOwnerStatus.Matched, owner.Status);
        Assert.Equal(Environment.ProcessId, owner.ProcessId);
        var identity = ProcessIdentityReader.Read(owner.ProcessId!.Value);
        Assert.Equal(Environment.ProcessId, identity.ProcessId);
        Assert.Equal(64, identity.ImageSha256.Length);
        Assert.False(string.IsNullOrEmpty(identity.UserSid));
        Assert.Equal(identity, ProcessIdentityReader.Read(identity.ProcessId));
    }

    [Fact(DisplayName = "A Child Request Cannot Replace Its Transport Identity With A Claimed Parent Pid")]
    public async Task RejectsClaimedParents()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "native-peer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ready = Path.Combine(root, "ready.json");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var job = new OwnedWorkerJob();
        var registered = job.Start(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!,
            ["tcp-client", ((IPEndPoint)listener.LocalEndpoint).Port.ToString(), ready, Environment.ProcessId.ToString()], root,
            Guid.NewGuid(), Guid.NewGuid(), identity =>
            {
                using var file = new FileStream(Path.Combine(root, "identity.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                file.Write(JsonSerializer.SerializeToUtf8Bytes(identity));
                file.Flush(flushToDisk: true);
            });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
        using var reader = new StreamReader(connection.GetStream());
        var line = await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("Missing controlled peer message");
        using var body = JsonDocument.Parse(line);
        Assert.Equal(Environment.ProcessId, body.RootElement.GetProperty("claimedProcessId").GetInt32());
        var owner = LoopbackTcpPeer.Observe((IPEndPoint)connection.Client.RemoteEndPoint!, (IPEndPoint)connection.Client.LocalEndPoint!);
        Assert.Equal(TcpOwnerStatus.Matched, owner.Status);
        Assert.Equal(registered.ProcessId, owner.ProcessId);
        Assert.NotEqual(Environment.ProcessId, owner.ProcessId);
        var identity = ProcessIdentityReader.Read(owner.ProcessId!.Value);
        Assert.Equal(registered.CreatedAtUtc, identity.CreatedAtUtc);
        Assert.Equal(registered.ImagePath, identity.ImagePath);
        Assert.Equal(registered.ImageSha256, identity.ImageSha256);
        Assert.Equal(registered.UserSid, identity.UserSid);
        Assert.Equal(registered.IsElevated, identity.IsElevated);
        using var accepted = new NativeClientBinding(identity);
        Assert.True(accepted.Matches((IPEndPoint)connection.Client.RemoteEndPoint!, (IPEndPoint)connection.Client.LocalEndPoint!));
        using var wrongOwner = new NativeClientBinding(Environment.ProcessId);
        Assert.False(wrongOwner.Matches((IPEndPoint)connection.Client.RemoteEndPoint!, (IPEndPoint)connection.Client.LocalEndPoint!));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory(DisplayName = "Unsupported Or Non Loopback Peers Cannot Be Classified As Local Native")]
    [InlineData("192.0.2.1", "127.0.0.1")]
    [InlineData("127.0.0.1", "192.0.2.1")]
    [InlineData("::1", "::1")]
    public void RejectsUnsupportedPeers(string client, string server)
    {
        Assert.Throws<ArgumentException>(() => LoopbackTcpPeer.Observe(new(IPAddress.Parse(client), 12345), new(IPAddress.Parse(server), 23456)));
    }

    [Fact(DisplayName = "A Listening Pair Without An Established Client Never Invents An Owner")]
    public void RejectsUnconnectedPairs()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        using var client = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        client.Start();
        var result = LoopbackTcpPeer.Observe((IPEndPoint)client.LocalEndpoint, (IPEndPoint)server.LocalEndpoint);
        Assert.Equal(TcpOwnerStatus.NotFound, result.Status);
        Assert.Null(result.ProcessId);
    }
}
