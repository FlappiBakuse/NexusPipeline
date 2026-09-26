using System.Text.Json;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Tests.Support;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class FrozenResourceScopeTests
{
    [Fact]
    public void RestartedOccurrenceKeepsLauncherRoleEncodingAndWritableDesktopScopes()
    {
        var script = new ScriptInstance
        {
            Id = "frozen", RootPath = Path.Combine(Path.GetTempPath(), "frozen-project"),
            MainExe = @"C:\SharedRuntime\python.exe", GameExe = @"C:\Games\game.exe", LaunchGame = true,
        };
        var spec = new ResolvedScriptSpec(script, "1", new(false, "javascript", "plugin", "", ""), "hash")
        { RootProcessRole = ProcessRole.GameLauncher, OutputEncoding = "utf-8" };
        var task = new PlannedQueueTask(new QueueTask { Id = "task", ScriptInstanceId = script.Id }, script, [], ResolvedSpec: spec);
        var queue = new DispatchQueue { Id = "queue", Tasks = [task.Task] };
        var plan = new QueueExecutionPlan(queue, [task], ExecutionAdmissionProfile.ForQueue(queue, [task]), 1);
        var frozen = JsonSerializer.Deserialize<FrozenQueuePlanData>(JsonSerializer.Serialize(ExecutionPlanBuilder.FreezeQueue(plan)))!;
        var scripts = new PluginAvailabilityPolicyTestsFixture.SingleScriptRepository(script);
        var queues = new PluginAvailabilityPolicyTestsFixture.EmptyQueueRepository();
        var users = new PluginAvailabilityPolicyTestsFixture.EmptyUserRepository();
        var availability = new PluginAvailabilityPolicyTestsFixture.FakePluginAvailability();
        var builder = new ExecutionPlanBuilder(scripts, queues, users, new ExecutionValidator(scripts, queues, users, availability));
        QueueExecutionPlan restored = builder.RestoreFrozenQueue(frozen);
        Assert.Equal(ProcessRole.GameLauncher, restored.Tasks[0].ResolvedSpec!.RootProcessRole);
        Assert.Equal("utf-8", restored.Tasks[0].ResolvedSpec!.OutputEncoding);
        Assert.Equal(plan.Admission.Resources.WritableRoots, restored.Admission.Resources.WritableRoots);
        Assert.Equal(plan.Admission.Resources.DesktopDomains, restored.Admission.Resources.DesktopDomains);

        // A snapshot made by an older reader omitted these scopes. Rebuild from
        // its frozen declarations instead of treating omission as no resource.
        frozen.Admission!.ResourceSchemaVersion = 0;
        frozen.Admission.DesktopDomains.Clear();
        frozen.Admission.WritableRoots.Clear();
        QueueExecutionPlan legacy = builder.RestoreFrozenQueue(frozen);
        Assert.NotEmpty(legacy.Admission.Resources.DesktopDomains);
        Assert.Contains(script.RootPath, legacy.Admission.Resources.WritableRoots);
    }
}
