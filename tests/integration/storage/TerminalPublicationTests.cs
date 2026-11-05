using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class TerminalPublicationTests
{
    [Theory(DisplayName = "Persisted Completion And Cancellation Permit A Definition Save Before Terminal Observers Return")]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("unstarted")]
    public async Task SavesBeforeObservers(string terminal)
    {
        Action<AttemptReceipt>? observe = null;
        var calls = 0;
        var observer = new CallbackObserver(receipt => { observe!(receipt); calls++; });
        using var fixture = new ExecutionRuntimeFixture(configuration: true,
            mode: terminal == "completed" ? "tail" : "wait", terminalObserver: observer);
        var original = File.ReadAllBytes(fixture.Source.Configuration);
        observe = receipt =>
        {
            var source = fixture.Source;
            Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
            Assert.Equal(original, File.ReadAllBytes(source.Configuration));
            var catalog = source.Definitions.Read();
            var queue = catalog.Queues.Single() with { Name = "Saved at terminal observation" };
            var changes = new AutomationChanges(catalog.Revision, [], [], [], [], [queue], [], []);
            var identity = AutomationDefinitionStore.Identity(Guid.NewGuid(), "terminal-publication", changes);
            var service = new AutomationDefinitionService(source.Instance, new(), source.Database, source.Lease,
                fixture.Base.Resources, fixture.Base.Index);
            Assert.True(service.Change(identity, changes, DateTimeOffset.UtcNow).IsNew);
            Assert.False(service.Change(identity, changes, DateTimeOffset.UtcNow).IsNew);
            Assert.Equal(queue.Name, source.Definitions.Read().Queues.Single().Name);
        };
        if (terminal == "unstarted") fixture.Cancel();
        var running = fixture.Consumer.RunNextAsync();
        if (terminal == "cancelled")
        {
            await fixture.Ready(".output-ready");
            var catalog = fixture.Source.Definitions.Read();
            var change = new AutomationChanges(catalog.Revision, [], [], [], [], [catalog.Queues.Single() with { Name = "Still running" }], [], []);
            var service = new AutomationDefinitionService(fixture.Source.Instance, new(), fixture.Source.Database,
                fixture.Source.Lease, fixture.Base.Resources, fixture.Base.Index);
            var identity = AutomationDefinitionStore.Identity(Guid.NewGuid(), "terminal-publication", change);
            try
            {
                Assert.Equal("automation_host_unavailable", Assert.Throws<AutomationDefinitionException>(() =>
                    service.Change(identity, change, DateTimeOffset.UtcNow)).Message);
            }
            finally { fixture.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(45)); }
        }
        var receipt = Assert.IsType<AttemptReceipt>(await running.WaitAsync(TimeSpan.FromSeconds(45)));
        Assert.Equal(terminal == "completed" ? LifecycleOutcome.Completed : LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
        Assert.Equal(1, calls);
        Assert.Null(fixture.Consumer.FailureCode);
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Theory(DisplayName = "Definition Resource And Host Admission Failures Are Conflicts While Invalid Definitions Remain Bad Requests")]
    [InlineData("automation_resource_conflict", 409, "resource_conflict")]
    [InlineData("automation_host_unavailable", 409, "host_unavailable")]
    [InlineData("automation_queue_invalid", 400, "request_invalid")]
    public async Task MapsDefinitionAdmission(string reason, int status, string code)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        await using var output = new MemoryStream(); context.Response.Body = output;
        await ControlExceptionBoundary.InvokeAsync(context, _ => throw new AutomationDefinitionException(reason));
        Assert.Equal(status, context.Response.StatusCode);
        using var response = JsonDocument.Parse(output.ToArray());
        Assert.Equal(code, response.RootElement.GetProperty("code").GetString());
    }

    private sealed class CallbackObserver(Action<AttemptReceipt> callback) : IAttemptTerminalObserver
    {
        public void OnCommitted(AttemptReceipt receipt) => callback(receipt);
    }
}
