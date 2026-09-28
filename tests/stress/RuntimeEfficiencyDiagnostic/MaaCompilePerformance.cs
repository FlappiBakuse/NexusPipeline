using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.StressDiagnostics;

internal static class MaaCompilePerformance
{
    internal static int Measure(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("--maa-compile <actual-plugin-dll> <new-owned-directory> <source-label>");
        string dll = Path.GetFullPath(args[0]), root = Path.GetFullPath(args[1]);
        if (Directory.Exists(root)) throw new IOException("Performance fixture must be a new owned directory");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".nxp-maa-performance-owned"), args[2]);
        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        Type compilerType = assembly.GetType("NexusPipeline.Plugin.MaaFrameworkDriver.ProjectCompiler", true)!;
        Type profileType = assembly.GetType("NexusPipeline.Plugin.MaaFrameworkDriver.DriverProfile", true)!;
        var constructor = compilerType.GetConstructors().Single();
        var compile = compilerType.GetMethod("Compile")!;
        object NewCompiler(string folder, CancellationToken token) => constructor.Invoke(
            constructor.GetParameters().Length == 4 ? [folder, "interface.json", "zh_cn", token] : [folder, "interface.json", "zh_cn"]);
        object Compile(object compiler, object profile, CancellationToken token) => compile.Invoke(compiler,
            compile.GetParameters().Length == 2 ? [profile, token] : [profile])!;
        object? Counter(object compiler, string name) => compilerType.GetProperty(name)?.GetValue(compiler);
        var rows = new List<object>();
        using var process = Process.GetCurrentProcess();
        foreach (int tasks in new[] { 1, 10, 50 })
        foreach (int files in new[] { 1, 100 })
        {
            string folder = Path.Combine(root, $"tasks-{tasks}-files-{files}");
            Directory.CreateDirectory(Path.Combine(folder, "resource"));
            Directory.CreateDirectory(Path.Combine(folder, "native"));
            File.WriteAllText(Path.Combine(folder, "native", "MaaFramework.dll"), "controlled compiler bytes; never loaded");
            foreach (int index in Enumerable.Range(0, files))
                File.WriteAllBytes(Path.Combine(folder, "resource", index + ".bin"), new byte[64 * 1024]);
            var declarations = new JsonArray(Enumerable.Range(0, tasks).Select(index => (JsonNode?)new JsonObject
                { ["name"] = "t" + index, ["entry"] = "Entry" }).ToArray());
            File.WriteAllText(Path.Combine(folder, "interface.json"), new JsonObject
            {
                ["interface_version"] = 2, ["name"] = "Controlled measurement",
                ["controller"] = new JsonArray(new JsonObject { ["name"] = "PC", ["type"] = "Win32" }),
                ["resource"] = new JsonArray(new JsonObject { ["name"] = "R", ["path"] = new JsonArray("resource") }),
                ["task"] = declarations,
            }.ToJsonString());
            object profile = JsonSerializer.Deserialize(JsonSerializer.Serialize(new
            {
                ProfileId = "controlled", Revision = "1", PackageRoot = folder, Controller = "PC", Resource = "R",
                NativeDirectory = "native", NativeVersion = "v5.14.0", SelectedTasks = Enumerable.Range(0, tasks).Select(index => "t" + index),
            }), profileType)!;
            for (int sample = 0; sample < 30; sample++)
            {
                object? frozen = null;
                foreach (string phase in new[] { "preview-new-view", "preview-repeated-view", "authorize", "prepare", "run-revalidate", "run-repeated-revalidate" })
                {
                    process.Refresh();
                    TimeSpan cpu = process.TotalProcessorTime;
                    long allocated = GC.GetTotalAllocatedBytes(true), working = process.WorkingSet64;
                    var wall = Stopwatch.StartNew();
                    object compiler = phase == "preview-repeated-view" ? frozen! : NewCompiler(folder, default);
                    object plan = Compile(compiler, profile, default);
                    double milliseconds = wall.Elapsed.TotalMilliseconds;
                    frozen = compiler;
                    string fingerprint = (string)plan.GetType().GetProperty("ExecutionFingerprint")!.GetValue(plan)!;
                    if (fingerprint.Length != 64) throw new InvalidDataException("Actual compiler returned no identity");
                    process.Refresh();
                    rows.Add(new { tasks, files, sample, phase, milliseconds,
                        cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                        allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, workingSetBefore = working,
                        workingSetAfter = process.WorkingSet64, filesParsed = Counter(compiler, "FilesParsed"),
                        parsedBytes = Counter(compiler, "ParsedBytes"), filesHashed = Counter(compiler, "FilesHashed"),
                        hashedBytes = Counter(compiler, "BytesRead"), entriesEnumerated = Counter(compiler, "EntriesEnumerated") });
                }
            }
        }
        var cancellations = new List<object>();
        bool cancellationFailed = false;
        string cancellationRoot = Path.Combine(root, "tasks-1-files-100");
        using (var bytes = File.Create(Path.Combine(cancellationRoot, "resource", "cancel.bin"))) bytes.SetLength(512L * 1024 * 1024);
        object cancellationProfile = JsonSerializer.Deserialize(JsonSerializer.Serialize(new
        { PackageRoot = cancellationRoot, Controller = "PC", Resource = "R", NativeDirectory = "native", NativeVersion = "v5.14.0", SelectedTasks = new[] { "t0" } }), profileType)!;
        for (int sample = 0; sample < 30; sample++)
        {
            using var stop = new CancellationTokenSource();
            object compiler = NewCompiler(cancellationRoot, stop.Token);
            var producer = Task.Run(() => Compile(compiler, cancellationProfile, stop.Token));
            Thread.Sleep(5); stop.Cancel(); var wall = Stopwatch.StartNew();
            bool observed = false;
            try { producer.WaitAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult(); }
            catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { observed = true; }
            catch (OperationCanceledException) { observed = true; }
            catch (TimeoutException) { }
            double milliseconds = wall.Elapsed.TotalMilliseconds;
            object? stoppedBytes = Counter(compiler, "BytesRead");
            Thread.Sleep(100);
            bool stable = stoppedBytes is not null && Equals(stoppedBytes, Counter(compiler, "BytesRead"));
            bool producerCompleted = producer.IsCompleted;
            cancellationFailed |= !observed || !stable || !producerCompleted;
            try { producer.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
            catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { }
            catch (OperationCanceledException) { }
            cancellations.Add(new { sample, observed, producerCompleted, milliseconds, stoppedBytes, stable });
        }
        var events = new List<object>();
        for (int sample = 0; sample < 30; sample++)
        {
            var plan = new PluginProviderPlan("controlled", "1", "auth", [], [new("task", "Task", 0)], new());
            var projection = new ProviderTaskProjection(plan, "controlled", "1", "record", "user", "script", 1);
            var wall = Stopwatch.StartNew();
            long allocated = GC.GetTotalAllocatedBytes(true);
            for (int index = 1; index <= 10000; index++)
                projection.Accept(new("progress", index, null, null, new() { ["code"] = "controlled" }));
            projection.Accept(new("task_event", 10001, "task", "running", new()));
            projection.Accept(new("task_event", 10002, "task", "succeeded", new()));
            projection.Accept(new("completed", 10003, null, "succeeded", new()));
            projection.Finish("succeeded", false);
            var snapshot = projection.Snapshot();
            events.Add(new { sample, milliseconds = wall.Elapsed.TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, snapshot });
        }
        File.WriteAllText(Path.Combine(root, "samples.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, source = args[2], pluginDll = dll, pluginSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).ToLowerInvariant(),
            samples = rows, progress10000 = events, cancellations,
            limitations = new[] { "New-view means uncached compiler objects; the OS file cache is not flushed.",
                "Authorization/prepare/run rows measure their actual shared compiler boundary, excluding provider storage and native startup.",
                "CPU uses process clock granularity; counters are per compile except parsed bytes retained by the immutable view.",
                "User project content is rehashed at authorization and execution boundaries; repeated runs do not trust mtime or length." },
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Maa compiler: {rows.Count} raw measurements and {events.Count} progress samples -> {root}");
        return cancellationFailed ? 1 : 0;
    }
}
