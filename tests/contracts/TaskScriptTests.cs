using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class TaskScriptTests
{
    [Theory]
    [InlineData(96, true)]
    [InlineData(160, false)]
    public void BoundsSourceAllocation(int megabytes, bool accepted)
    {
        const string source = "globalThis.nexusTaskAdapter = input => { const value = nexusReadSource('fixture.json'); return { value }; };";
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
        var script = new BoundedTaskScript(source, digest);
        byte[] Execute() => script.Execute("{}"u8.ToArray(), default, null, _ =>
        {
            var bytes = new byte[megabytes * 1024 * 1024];
            GC.KeepAlive(bytes);
            return "read";
        });
        if (accepted)
        {
            using var result = JsonDocument.Parse(Execute());
            Assert.Equal("read", result.RootElement.GetProperty("value").GetString());
        }
        else Assert.NotNull(Record.Exception(() => Execute()));
    }

    [Fact(DisplayName = "Actual Interpreter Has No Clr Filesystem Network Or Node Api")]
    public async Task RestrictsInterpreterApis()
    {
        await using var fixture = new TaskScriptFixture();
        await fixture.ActivateAsync();
        var result = fixture.Execute(fixture.Executor("""
            globalThis.nexusTaskAdapter = input => ({...input, direction:'response', resources:undefined,
              tasks:[{taskId:'probe', title:[typeof System,typeof process,typeof require,typeof fetch].join(','),
                isSelected:true,isEnabled:true,dependencies:[],retrySafety:'unknown'}]});
            """));
        Assert.Equal("undefined,undefined,undefined,undefined", Assert.Single(result.Tasks!).Title);
    }

    [Fact(DisplayName = "Each Invocation Has Fresh Script Globals")]
    public async Task IsolatesInvocationGlobals()
    {
        await using var fixture = new TaskScriptFixture();
        await fixture.ActivateAsync();
        var executor = fixture.Executor("""
            globalThis.calls = (globalThis.calls || 0) + 1;
            globalThis.nexusTaskAdapter = input => ({...input, direction:'response', resources:undefined,
              tasks:[{taskId:'probe', title:String(globalThis.calls),isSelected:true,isEnabled:true,dependencies:[],retrySafety:'unknown'}]});
            """);
        Assert.Equal("1", Assert.Single(fixture.Execute(executor).Tasks!).Title);
        Assert.Equal("1", Assert.Single(fixture.Execute(executor).Tasks!).Title);
    }

    [Theory(DisplayName = "Wrong Wire Identity Is Rejected After Actual Script Execution")]
    [InlineData("version")]
    [InlineData("operationId")]
    [InlineData("planDigest")]
    public async Task RejectsWrongIdentity(string field)
    {
        await using var fixture = new TaskScriptFixture();
        await fixture.ActivateAsync();
        var value = field == "version" ? "99" : field == "operationId" ? "'00000000-0000-4000-8000-000000000001'" : "'" + new string('b', 64) + "'";
        var script = "globalThis.nexusTaskAdapter = input => ({...input,direction:'response',resources:undefined,tasks:[]," + field + ":" + value + "});";
        Assert.Throws<JsonException>(() => fixture.Execute(fixture.Executor(script)));
    }

    [Fact(DisplayName = "Resource Digest Is Checked Before Untrusted Script Runs")]
    public async Task VerifiesResourceDigest()
    {
        await using var fixture = new TaskScriptFixture();
        await fixture.ActivateAsync();
        fixture.Request = fixture.Request with { Resources = [new() { ResourceId = "config", Sha256 = new string('0', 64), Content = "{}" }] };
        Assert.Throws<JsonException>(() => fixture.Execute(fixture.Executor("throw new Error('must_not_execute');")));
    }

    [Fact(DisplayName = "Pending Or Disposed Activation Cannot Invoke Retained Executor")]
    public async Task RevokesRetainedExecutors()
    {
        await using var fixture = new TaskScriptFixture();
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Execute(fixture.Executor()));
        await fixture.ActivateAsync();
        var executor = fixture.Executor();
        Assert.Empty(fixture.Execute(executor).Tasks!);
        await fixture.DisposeAsync();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Execute(executor));
    }

    [Fact(DisplayName = "Missing Capability Cannot Run An Otherwise Valid Script")]
    public async Task RequiresScriptCapability()
    {
        await using var fixture = new TaskScriptFixture();
        fixture.Manifest = fixture.Manifest with { Capabilities = ["configuration.edit"] };
        await fixture.ActivateAsync();
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Execute(fixture.Executor()));
    }

    [Fact(DisplayName = "Unbounded Loop Is Stopped By Interpreter Budget")]
    public async Task BoundsInterpreterExecution()
    {
        await using var fixture = new TaskScriptFixture();
        await fixture.ActivateAsync();
        var execution = Task.Run(() => fixture.Execute(fixture.Executor("while(true) {}")));
        var exception = await Record.ExceptionAsync(async () => await execution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(execution.IsCompleted, "The interpreter must stop before the outer test deadline");
        Assert.NotNull(exception);
        Assert.Empty(fixture.Execute(fixture.Executor()).Tasks!);
    }

    [Fact(DisplayName = "A Host Compiled Read Exceeding The Actual Interpreter Deadline Returns A Typed Script Failure")]
    public void BoundsCompiledRead()
    {
        const string source = "globalThis.nexusTaskAdapter = () => JSON.parse(nexusReadCompiled('execution', '/Tasks'));";
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
        var script = new BoundedTaskScript(source, digest);
        var error = Assert.Throws<IOException>(() => script.Execute("{}"u8.ToArray(), default,
            (_, _) => { Thread.Sleep(2100); return "[]"; }));
        Assert.Equal("task_script_timeout", error.Message);
    }

    [Fact(DisplayName = "Retry Proposal Cannot Upgrade Frozen Unknown Safety")]
    public async Task PreservesFrozenSafety()
    {
        await using var fixture = new TaskScriptFixture();
        await fixture.ActivateAsync();
        fixture.Request = fixture.Request with
        {
            Phase = "retry", PlanDigest = new string('a', 64), RunId = Guid.NewGuid().ToString("D"), AttemptId = Guid.NewGuid().ToString("D"), IdempotencyKey = "frozen-key",
            Tasks = [new() { TaskId = "unsafe", Title = "Unknown", IsSelected = true, IsEnabled = true, Dependencies = [], RetrySafety = "unknown" }]
        };
        var script = "globalThis.nexusTaskAdapter = input => ({...input,direction:'response',resources:undefined,selectedTaskIds:['unsafe']});";
        Assert.Throws<JsonException>(() => fixture.Execute(fixture.Executor(script)));
    }

    [Theory(DisplayName = "Actual Adapter Discovery Binds Explicit Task Projection To The Original Resource And Frozen Dependency Graph")]
    [InlineData("valid", true)]
    [InlineData("resource", false)]
    [InlineData("path", false)]
    [InlineData("root", false)]
    [InlineData("prerequisite", false)]
    public async Task BindsTaskProjection(string fault, bool allowed)
    {
        await using var fixture = new TaskScriptFixture();
        await fixture.ActivateAsync();
        const string content = "{\"enabled\":true}";
        fixture.Request = fixture.Request with { Resources = [new() { ResourceId = "config", Content = content,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))) }] };
        var resource = fixture.Request.Resources!.Single();
        var projection = new { resourceId = fault == "resource" ? "other" : resource.ResourceId,
            relativePath = fault == "path" ? "other.json" : resource.RelativePath ?? "",
            selector = fault == "root" ? Array.Empty<string>() : new[] { "enabled" } };
        var tasks = new[] { new { taskId = "task", title = "Task", isSelected = true, isEnabled = true,
            dependencies = Array.Empty<string>(), retrySafety = "safe", projection,
            retryPrerequisites = fault == "prerequisite" ? new[] { "unknown" } : [] } };
        var script = "globalThis.nexusTaskAdapter = input => ({...input,direction:'response',resources:undefined,tasks:" +
            JsonSerializer.Serialize(tasks) + "});";
        if (allowed) Assert.Equal(resource.ResourceId, Assert.Single(fixture.Execute(fixture.Executor(script)).Tasks!).Projection!.ResourceId);
        else Assert.Throws<JsonException>(() => fixture.Execute(fixture.Executor(script)));
    }

    [Theory(DisplayName = "The Actual Adapter Executor Does Not Treat Technical Or Cleanup Flags As Business Retry Authority")]
    [InlineData("business", true)]
    [InlineData("technical", false)]
    [InlineData("cleanup", false)]
    public async Task PreservesRetryRoles(string role, bool allowed)
    {
        await using var fixture = new TaskScriptFixture();
        await fixture.ActivateAsync();
        fixture.Request = fixture.Request with
        {
            Phase = "retry", PlanDigest = new string('a', 64), RunId = Guid.NewGuid().ToString("D"), AttemptId = Guid.NewGuid().ToString("D"), IdempotencyKey = "frozen-key",
            Tasks = [new() { TaskId = "target", Title = "Target", IsSelected = true, IsEnabled = true, Dependencies = [], RetrySafety = "safe", WorkflowRole = role }]
        };
        var script = "globalThis.nexusTaskAdapter = input => ({...input,direction:'response',resources:undefined,selectedTaskIds:['target']});";
        if (allowed) Assert.Equal("target", Assert.Single(fixture.Execute(fixture.Executor(script)).SelectedTaskIds!));
        else Assert.Throws<JsonException>(() => fixture.Execute(fixture.Executor(script)));
    }

    [Theory(DisplayName = "Observation Invocation Selection Must Refer To Enabled Frozen Tasks")]
    [InlineData("selected", true)]
    [InlineData("disabled", false)]
    [InlineData("foreign", false)]
    public async Task BindsObservationSelection(string target, bool allowed)
    {
        await using var fixture = new TaskScriptFixture();
        await fixture.ActivateAsync();
        fixture.Request = fixture.Request with
        {
            Phase = "observe", PlanDigest = new string('a', 64), RunId = Guid.NewGuid().ToString("D"), AttemptId = Guid.NewGuid().ToString("D"),
            SelectedTaskIds = [target], Tasks =
            [new() { TaskId = "selected", Title = "Selected", IsSelected = true, IsEnabled = true, Dependencies = [], RetrySafety = "safe" },
             new() { TaskId = "disabled", Title = "Disabled", IsSelected = false, IsEnabled = false, Dependencies = [], RetrySafety = "safe" }]
        };
        var script = "globalThis.nexusTaskAdapter = input => ({...input,direction:'response',resources:undefined,selectedTaskIds:undefined,observations:[]});";
        if (allowed) Assert.Empty(fixture.Execute(fixture.Executor(script)).Observations!);
        else Assert.Throws<JsonException>(() => fixture.Execute(fixture.Executor(script)));
    }

    [Theory(DisplayName = "Installed Adapter Discovery Freezes Native Continuation Authority To Explicit Selected Tasks")]
    [InlineData("login", true)]
    [InlineData("disabled", false)]
    [InlineData("foreign", false)]
    public async Task BindsNativeScope(string member, bool allowed)
    {
        await using var fixture = new TaskScriptFixture();
        await fixture.ActivateAsync();
        var tasks = new object[]
        {
            new { taskId = "login", title = "Login", isSelected = true, isEnabled = true, dependencies = Array.Empty<string>(), retrySafety = "unknown", workflowRole = "technical" },
            new { taskId = "daily", title = "Daily", isSelected = true, isEnabled = true, dependencies = new[] { "login" }, retrySafety = "safe", nativeResumeScope = new[] { "daily", member } },
            new { taskId = "disabled", title = "Disabled", isSelected = false, isEnabled = false, dependencies = Array.Empty<string>(), retrySafety = "unknown" }
        };
        var script = "globalThis.nexusTaskAdapter = input => ({...input,direction:'response',resources:undefined,tasks:" + JsonSerializer.Serialize(tasks) + "});";
        if (allowed) Assert.Equal(new[] { "daily", "login" },
            fixture.Execute(fixture.Executor(script)).Tasks!.Single(task => task.TaskId == "daily").NativeResumeScope);
        else Assert.Throws<JsonException>(() => fixture.Execute(fixture.Executor(script)));
    }
}
