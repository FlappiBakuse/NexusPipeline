using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.StressDiagnostics;

internal static class TaskDiscoveryPerformance
{
    internal static int Measure(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("--task-discovery <new-owned-directory> <source-label>");
        string root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root)) throw new IOException("Performance fixture must be a new owned directory");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".nxp-discovery-owned"), args[1]);
        var samples = new List<object>();
        using Process process = Process.GetCurrentProcess();
        foreach (int resources in new[] { 1, 10, 50 })
        {
            string folder = Path.Combine(root, "resources-" + resources);
            Directory.CreateDirectory(folder);
            foreach (int index in Enumerable.Range(0, resources))
                File.WriteAllText(Path.Combine(folder, index + ".json"), JsonSerializer.Serialize(new {
                    f0 = 0, f1 = true, f2 = "fixture", f3 = 3, f4 = false, f5 = "value", f6 = 6, f7 = true,
                    padding = new string('p', 2048),
                }));
            var protocol = new TaskProtocolDescriptor("0.1.0", """
                  const fields=[];
                  for(const resource of input.configResources) {
                    const first=nexus.readConfig(resource.id);
                    const second=nexus.readConfig(resource.id);
                    if(first.document.f0!==second.document.f0) throw Error('inconsistent frozen bytes');
                    for(let f=0;f<8;f++) fields.push({resourceId:resource.id,selector:['f'+f]});
                  }
                  console.log({protocolVersion:'0.1.0',type:'discovery',coverage:'complete',tasks:[],diagnostics:[],
                    behaviorFields:fields,configAssessment:{schemaVersion:'1',checks:[{
                      ruleId:'fixture.reads',evaluation:'satisfied',severity:'info',executionEffect:'none',
                      scope:{kind:'binding'},locations:[],actions:[],
                      reasonText:{kind:'literal',value:'Controlled read-only fixture'}}]}});
                """, "", "", []) { ConfigRules = [new("fixture.reads", true, "critical_when_applicable")] };
            for (int sample = 0; sample < 30; sample++)
            {
                process.Refresh();
                TimeSpan coldCpuBefore = process.TotalProcessorTime;
                long coldWorkingSetBefore = process.WorkingSet64;
                int[] coldGcBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
                var timer = Stopwatch.StartNew();
                long allocated = GC.GetTotalAllocatedBytes(true);
                TaskConfigView view;
                object coldCounters;
                using (var metrics = TaskConfigMetrics.Begin())
                {
                    view = new TaskConfigView();
                    foreach (int index in Enumerable.Range(0, resources))
                        view.AddConfig("config:r" + index, Path.Combine(folder, index + ".json"), "json");
                    TaskPlan plan = TaskDiscoveryService.DiscoverAsync(protocol, view, "fixture", "1", "owned-user", "owned-script",
                        "en-US", true, default).GetAwaiter().GetResult();
                    if (plan.BehaviorSignature.Length != 64) throw new InvalidDataException("No actual discovery output");
                    coldCounters = metrics.Snapshot();
                }
                double coldMs = timer.Elapsed.TotalMilliseconds;
                long coldAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
                process.Refresh();
                double coldCpuMs = (process.TotalProcessorTime - coldCpuBefore).TotalMilliseconds;
                long coldWorkingSetAfter = process.WorkingSet64;
                int[] coldGcCollections = Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - coldGcBefore[g]).ToArray();
                TimeSpan warmCpuBefore = process.TotalProcessorTime;
                long warmWorkingSetBefore = process.WorkingSet64;
                int[] warmGcBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
                timer.Restart();
                allocated = GC.GetTotalAllocatedBytes(true);
                object warmCounters;
                using (var metrics = TaskConfigMetrics.Begin())
                {
                    TaskDiscoveryService.DiscoverAsync(protocol, view, "fixture", "1", "owned-user", "owned-script",
                        "en-US", true, default).GetAwaiter().GetResult();
                    warmCounters = metrics.Snapshot();
                }
                double warmMs = timer.Elapsed.TotalMilliseconds;
                long warmAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
                process.Refresh();
                samples.Add(new { resources, sample, coldMs, warmMs,
                    coldAllocated, warmAllocated, coldCounters, warmCounters,
                    coldCpuMs, warmCpuMs = (process.TotalProcessorTime - warmCpuBefore).TotalMilliseconds,
                    coldWorkingSetBefore, coldWorkingSetAfter, warmWorkingSetBefore, warmWorkingSetAfter = process.WorkingSet64,
                    coldGcCollections, warmGcCollections = Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - warmGcBefore[g]).ToArray() });
            }
        }
        string json = JsonSerializer.Serialize(new { schemaVersion = 1, sourceLabel = args[1],
            clock = "Stopwatch", samplesPerSize = 30, fieldsPerResource = 8, samples, failures = 0,
            os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(root, "task-discovery-performance.json"), json);
        Console.WriteLine(json);
        return 0;
    }
}
