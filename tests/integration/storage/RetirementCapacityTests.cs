using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetirementCapacityTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "Two Actual Run Loops Expose Their Admission Runtime Projection Retirement And Cleanup Allocation Costs")]
    public async Task MeasuresAllocationCosts()
    {
        var phases = new List<object>();
        var measured = await RetirementCapacityScenario.MeasureAsync(2, (run, phase, milliseconds, bytes) =>
            phases.Add(new { run, phase, elapsedMilliseconds = milliseconds, allocatedBytes = bytes }));
        Assert.Equal(10, phases.Count);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            scope = "Two actual run loops with phase boundaries; diagnostic only, no capacity gate",
            actualRuns = 2, measured.Elapsed.TotalMilliseconds, managedAllocatedBytes = measured.Allocated,
            maximumSampledManagedBytes = measured.ManagedBytes, phases,
            limits = "Fixture preparation, final EXPLAIN and disposal excluded; process-wide cumulative managed allocation, no working-set or continuously sampled peak claim"
        }));
    }

    [Fact(DisplayName = "Eight Actual Completed Runs Can Retire Their Original Logs Without Losing Indexed Metadata")]
    public async Task RetiresIndexedRuns()
    {
        var measured = await RetirementCapacityScenario.MeasureAsync(8);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            scope = "Eight actual main/pre/post runs through final record, exact log retirement and existing cleanup; diagnostic pilot only",
            actualRuns = 8, measured.Elapsed.TotalMilliseconds, managedAllocatedBytes = measured.Allocated,
            maximumSampledManagedBytes = measured.ManagedBytes, actualQueryPlan = measured.QueryPlan,
            limits = "Whole experiment allocation and per-run managed samples; no large dataset, process working set, startup or desktop memory claim"
        }));
        Assert.True(measured.Elapsed > TimeSpan.Zero);
        Assert.True(measured.Allocated > 0); Assert.True(measured.ManagedBytes > 0);
        Assert.Contains(measured.QueryPlan, line => line.Contains("SEARCH c", StringComparison.Ordinal));
        Assert.Contains(measured.QueryPlan, line => line.Contains("SEARCH r", StringComparison.Ordinal));
        Assert.DoesNotContain(measured.QueryPlan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }
}
