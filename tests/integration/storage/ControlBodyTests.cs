using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ControlBodyTests
{
    [Theory(DisplayName = "Tiny Json Requests Use Small Read Windows With Or Without A Declared Length")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsTinyBodies(bool declared)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"value\":\"中文\"}");
        using var source = new ObservedStream(bytes);
        var value = await ReadAsync(source, declared ? bytes.Length : null, 2 * 1024 * 1024);
        Assert.Equal("中文", value.GetProperty("value").GetString());
        Assert.InRange(source.LargestWindow, 1, 8192);
    }

    [Theory(DisplayName = "Streamed Json Can Grow Within Its Byte Budget Without Treating Length Hints As Authority")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsGrowingBodies(bool inaccurateHint)
    {
        using var source = new ObservedStream(Encoding.UTF8.GetBytes("{\"value\":\"" + new string('x', 70000) + "\"}"));
        var value = await ReadAsync(source, inaccurateHint ? 3 : null, 100000);
        Assert.Equal(70000, value.GetProperty("value").GetString()!.Length);
        Assert.Equal(source.Length, source.BytesRead);
    }

    [Theory(DisplayName = "Declared And Streamed Oversized Bodies Remain Rejected At The Actual Byte Boundary")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsOversizedBodies(bool declared)
    {
        using var source = new ObservedStream(Encoding.UTF8.GetBytes("{\"value\":\"" + new string('x', 1000) + "\"}"));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ReadAsync(source, declared ? source.Length : null, 256));
        Assert.Equal("request_too_large", error.Message);
        Assert.Equal(413, error.GetType().GetProperty("Status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(error));
        Assert.Equal(declared ? 0 : 257, source.BytesRead);
    }

    private static async Task<JsonElement> ReadAsync(Stream source, long? length, int maximum)
    {
        var context = new DefaultHttpContext(); context.Request.ContentType = "application/json";
        context.Request.Body = source; context.Request.ContentLength = length;
        var type = Assembly.Load("NexusPipeline.Host").GetType("NexusPipeline.Host.Control.Http.ControlRequestBody", throwOnError: true)!;
        var method = type.GetMethod("ReadAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(null, [context, maximum]));
        await task;
        using var body = (IDisposable)task.GetType().GetProperty("Result")!.GetValue(task)!;
        return ((JsonElement)body.GetType().GetProperty("Root", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(body)!).Clone();
    }

    private sealed class ObservedStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int LargestWindow { get; private set; }
        internal long BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LargestWindow = Math.Max(LargestWindow, buffer.Length);
            var count = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 257)], cancellationToken);
            BytesRead += count; return count;
        }
    }
}
