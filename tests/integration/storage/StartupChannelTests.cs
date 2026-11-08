using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NexusPipeline.Host.Desktop;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class StartupChannelTests
{
    [Theory(DisplayName = "Startup Channel Rejects An Unbound Process Or Incorrect Nonce")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsForeignPeer(bool foreignProcess)
    {
        using var channel = new StartupChannel();
        channel.Bind(foreignProcess ? Environment.ProcessId + 1 : Environment.ProcessId);
        using var client = new NamedPipeClientStream(".", channel.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(deadline.Token);
        if (!foreignProcess) await client.WriteAsync(Encoding.UTF8.GetBytes("{\"kind\":\"hello\",\"nonce\":\"foreign\"}\n"), deadline.Token);
        await Assert.ThrowsAsync<IOException>(() => channel.SendAsync(new { kind = "stage", stage = 0 }).WaitAsync(deadline.Token));
    }

    [Fact(DisplayName = "Bound Startup Channel Delivers Stages And Refuses Unknown Actions")]
    public async Task BoundsStartupActions()
    {
        using var channel = new StartupChannel();
        channel.Bind(Environment.ProcessId);
        using var client = new NamedPipeClientStream(".", channel.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(deadline.Token);
        await client.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind = "hello", nonce = channel.Nonce }) + "\n"), deadline.Token);
        var sending = channel.SendAsync(new { kind = "stage", stage = 4 });
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        using var stage = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
        await sending.WaitAsync(deadline.Token);
        Assert.Equal(4, stage.RootElement.GetProperty("stage").GetInt32());
        await client.WriteAsync(Encoding.UTF8.GetBytes("{\"kind\":\"execute\"}\n"), deadline.Token);
        await channel.Dismissed.WaitAsync(deadline.Token);
        Assert.Null(await reader.ReadLineAsync(deadline.Token));
    }
}
