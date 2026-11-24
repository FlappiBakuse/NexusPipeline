using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusPipeline.Core.Operations;
using NexusPipeline.Host.Control.Http;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ControlErrorTests
{
    [Fact(DisplayName = "A permanent control failure correlates its response with one safe primary and cleanup diagnostic")]
    public async Task CorrelatesSanitizedFailure()
    {
        const string secret = "BearerValueMustNeverReachTheDiagnostic";
        using var logs = new CaptureLogger();
        using var services = new ServiceCollection().AddSingleton<ILoggerFactory>(logs).BuildServiceProvider();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/control/v1/records/private-path";
        context.Request.QueryString = new("?privateParameter=" + secret);
        context.Request.Headers.Authorization = "Bearer " + secret;
        context.Response.Body = body;
        var instanceId = Guid.NewGuid();
        await ControlExceptionBoundary.InvokeAsync(context, _ =>
            throw new AggregateException(new SqliteException(secret, 11), new IOException(secret)), instanceId);

        Assert.Equal(500, context.Response.StatusCode);
        body.Position = 0;
        using var response = await JsonDocument.ParseAsync(body);
        Assert.Equal("control_failed", response.RootElement.GetProperty("code").GetString());
        var correlation = response.RootElement.GetProperty("correlationId").GetGuid();
        Assert.NotEqual(Guid.Empty, correlation);
        var logged = Assert.Single(logs.Messages);
        Assert.Contains(correlation.ToString("D"), logged);
        Assert.Contains(instanceId.ToString("D"), logged);
        Assert.Contains(nameof(SqliteException), logged);
        Assert.Contains(nameof(IOException), logged);
        Assert.DoesNotContain(secret, logged);
        Assert.DoesNotContain("private-path", logged);
        Assert.DoesNotContain(secret, response.RootElement.GetRawText());
    }

    [Fact(DisplayName = "Expected control conflicts and request cancellation leave the failure log quiet")]
    public async Task LeavesExpectedFailuresQuiet()
    {
        using var logs = new CaptureLogger();
        using var services = new ServiceCollection().AddSingleton<ILoggerFactory>(logs).BuildServiceProvider();
        using var rejected = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = rejected;
        await ControlExceptionBoundary.InvokeAsync(context, _ => throw new OperationConflictException());
        Assert.Equal(409, context.Response.StatusCode);
        rejected.Position = 0;
        using var response = await JsonDocument.ParseAsync(rejected);
        Assert.Equal("operation_conflict", response.RootElement.GetProperty("code").GetString());

        using var caller = new CancellationTokenSource();
        caller.Cancel();
        context = new DefaultHttpContext { RequestServices = services, RequestAborted = caller.Token };
        await ControlExceptionBoundary.InvokeAsync(context, _ => Task.FromCanceled(caller.Token));
        Assert.Empty(logs.Messages);
    }

    private sealed class CaptureLogger : ILoggerFactory, ILogger
    {
        internal List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) =>
            categoryName.StartsWith("NexusPipeline.", StringComparison.Ordinal) ? this : NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, error) + error);
    }
}
