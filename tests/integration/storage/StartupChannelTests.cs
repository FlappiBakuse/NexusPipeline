using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NexusPipeline.Host.Desktop;
using NexusPipeline.Platform.Storage;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class StartupChannelTests(ITestOutputHelper output)
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

    [Fact(DisplayName = "A Real Startup Pipe Handles Split And Combined Frames With Bounded Managed Reads")]
    public async Task ReadsCombinedFrames()
    {
        var previous = Console.Error;
        using var observed = new StringWriter();
        try
        {
            Console.SetError(observed);
            using var profile = StartupProfile.Begin("startup-pipe-read-fixture");
            using var channel = new StartupChannel();
            channel.Bind(Environment.ProcessId);
            using var client = new NamedPipeClientStream(".", channel.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(deadline.Token);
            var hello = JsonSerializer.Serialize(new { kind = "hello", nonce = channel.Nonce }) + "\n";
            const int split = 7;
            await client.WriteAsync(Encoding.UTF8.GetBytes(hello[..split]), deadline.Token);
            await Task.Delay(30, deadline.Token);
            var combined = hello[split..] +
                "{\"kind\":\"measurement\",\"stage\":\"session\",\"elapsedMilliseconds\":1}\n" +
                "{\"kind\":\"measurement\",\"stage\":\"document\",\"elapsedMilliseconds\":2}\n";
            await client.WriteAsync(Encoding.UTF8.GetBytes(combined), deadline.Token);
            var sending = channel.SendAsync(new { kind = "stage", stage = 4 });
            using (var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true))
            using (var stage = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!))
                Assert.Equal(4, stage.RootElement.GetProperty("stage").GetInt32());
            await sending.WaitAsync(deadline.Token);
            await client.WriteAsync(Encoding.UTF8.GetBytes("{\"kind\":\"measurement\""), deadline.Token);
            client.Dispose();
            await channel.Dismissed.WaitAsync(deadline.Token);
        }
        finally { Console.SetError(previous); }
        var events = observed.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(events, value => value.Contains("\"stage\":\"session\"", StringComparison.Ordinal));
        Assert.Contains(events, value => value.Contains("\"stage\":\"document\"", StringComparison.Ordinal));
        using var result = JsonDocument.Parse(events.Single(value => value.Contains("\"phase\":\"startup-pipe-read-fixture\"", StringComparison.Ordinal)));
        var count = result.RootElement.GetProperty("steps").EnumerateArray().Single(step =>
            step.GetProperty("name").GetString() == "desktop.pipe.read").GetProperty("count").GetInt64();
        output.WriteLine($"managed named-pipe reads for split hello, two combined messages and partial EOF: {count}");
        Assert.InRange(count, 2, 6);
    }

    [Fact(DisplayName = "Startup Accepts A Maximum Frame And Refuses The Next Frame One Byte Too Large")]
    public async Task BoundsFrameAtDelimiter()
    {
        using var channel = new StartupChannel();
        channel.Bind(Environment.ProcessId);
        using var client = new NamedPipeClientStream(".", channel.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await client.ConnectAsync(deadline.Token);
        const int limit = 16384;
        var minimum = $"{{\"kind\":\"hello\",\"nonce\":\"{channel.Nonce}\",\"padding\":\"\"}}";
        var hello = Encoding.UTF8.GetBytes(minimum.Insert(minimum.Length - 2, new string('a', limit - minimum.Length)) + "\n");
        await client.WriteAsync(hello, deadline.Token);
        var sending = channel.SendAsync(new { kind = "stage", stage = 1 });
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        using var stage = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
        await sending.WaitAsync(deadline.Token);
        Assert.Equal(1, stage.RootElement.GetProperty("stage").GetInt32());
        var overlong = new byte[limit + 1];
        Array.Fill(overlong, (byte)'x');
        await client.WriteAsync(overlong, deadline.Token);
        await channel.Dismissed.WaitAsync(deadline.Token);
        Assert.Null(await reader.ReadLineAsync(deadline.Token));
    }
}
