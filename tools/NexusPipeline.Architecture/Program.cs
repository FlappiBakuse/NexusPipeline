using System.Text.Json;

namespace NexusPipeline.Architecture;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Parse(args);
            if (options.Command is null) return Usage("A command is required.");
            if (options.Command is not ("analyze" or "check" or "map" or "baseline-init" or "baseline-prune"))
            {
                return Usage($"Unknown command: {options.Command}");
            }

            var root = Path.GetFullPath(options.Root ?? Directory.GetCurrentDirectory());
            var mode = options.Mode ?? "production";
            var plan = options.FilePlan is null ? null : Path.GetFullPath(options.FilePlan);
            Console.WriteLine($"[architecture] command={options.Command} mode={mode}");
            Console.WriteLine("[architecture] loading MSBuild project");
            var loaded = await ProjectLoader.LoadAsync(root, mode, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine("[architecture] collecting compilation facts");
            var facts = await CompilationFactsBuilder.BuildAsync(loaded, CancellationToken.None).ConfigureAwait(false);
            var owners = new FileOwnerResolver(root, plan);
            var models = SymbolIndex.BuildModels(facts, owners);
            var declarations = SymbolIndex.CollectDeclarations(models);
            var edges = DependencyCollector.Collect(models, declarations);
            var violations = BoundaryRules.Evaluate(models, declarations, edges, owners);

            var report = new
            {
                schemaVersion = 1,
                status = "SUCCESS",
                root,
                mode,
                sourceFiles = facts.SourceFiles,
                referenceAssemblies = facts.ReferenceAssemblies,
                declarations,
                dependencies = edges,
                violations,
            };

            if (options.Command == "map")
            {
                var output = Require(options.Output, "--out");
                BackendMapWriter.Write(BackendMapWriter.Build(facts, declarations, edges), output);
                Console.WriteLine($"[architecture] map written: {output}");
                return 0;
            }

            if (options.Command == "baseline-init")
            {
                var output = Require(options.Output, "--out");
                if (File.Exists(output)) throw new InvalidOperationException($"Baseline already exists: {output}");
                BackendMapWriter.Write(report, output);
                Console.WriteLine($"[architecture] baseline written: {output}");
                return 0;
            }

            if (options.Command == "baseline-prune")
            {
                var baselinePath = Require(options.Baseline, "--baseline");
                var baseline = ReadViolations(baselinePath);
                var comparison = DebtBaseline.Compare(violations, baseline);
                var output = options.Output is null ? baselinePath : options.Output;
                BackendMapWriter.Write(new
                {
                    schemaVersion = 1,
                    status = "SUCCESS",
                    root,
                    mode,
                    violations = comparison.Added.Concat(comparison.Retained).ToArray(),
                }, output);
                Console.WriteLine($"[architecture] baseline pruned: removed={comparison.Removed.Count} retained={comparison.Retained.Count} added={comparison.Added.Count}");
                return comparison.IsAllowed ? 0 : 1;
            }

            if (options.Command == "check")
            {
                var baselinePath = Require(options.Baseline, "--baseline");
                var approved = ReadViolations(baselinePath);
                var comparison = DebtBaseline.Compare(violations, approved);
                Console.WriteLine($"[architecture] check added={comparison.Added.Count} removed={comparison.Removed.Count} retained={comparison.Retained.Count}");
                if (comparison.Added.Count > 0)
                {
                    foreach (var violation in comparison.Added.Take(50)) Console.Error.WriteLine($"{violation.RuleId} {violation.FilePath}:{violation.Line} {violation.Message}");
                    return 1;
                }
            }

            if (options.Output is not null)
            {
                BackendMapWriter.Write(report, options.Output);
                Console.WriteLine($"[architecture] report written: {options.Output}");
            }
            Console.WriteLine($"[architecture] files={facts.SourceFiles.Count} declarations={declarations.Count} edges={edges.Count} violations={violations.Count}");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"[architecture] ARGUMENT_ERROR: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[architecture] FAILED: {ex.Message}");
            return 1;
        }
    }

    private static IReadOnlyList<ArchitectureViolation> ReadViolations(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("violations", out var values)) return Array.Empty<ArchitectureViolation>();
        return values.Deserialize<List<ArchitectureViolation>>() ?? new List<ArchitectureViolation>();
    }

    private static string Require(string? value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException($"{name} is required.")
        : Path.GetFullPath(value);

    private static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        if (args.Length == 0) return options;
        options.Command = args[0];
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown argument: {argument}");
            if (index + 1 >= args.Length) throw new ArgumentException($"Value is missing for {argument}");
            var value = args[++index];
            switch (argument)
            {
                case "--root": options.Root = value; break;
                case "--mode": options.Mode = value; break;
                case "--file-plan": options.FilePlan = value; break;
                case "--out": options.Output = value; break;
                case "--baseline": options.Baseline = value; break;
                default: throw new ArgumentException($"Unknown argument: {argument}");
            }
        }
        return options;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine($"[architecture] ARGUMENT_ERROR: {message}");
        Console.Error.WriteLine("Usage: analyze|check|map|baseline-init|baseline-prune --root <host> --mode production|test-host|both [--file-plan <json>] [--out <json>] [--baseline <json>]");
        return 2;
    }

    private sealed class CliOptions
    {
        public string? Command { get; set; }
        public string? Root { get; set; }
        public string? Mode { get; set; }
        public string? FilePlan { get; set; }
        public string? Output { get; set; }
        public string? Baseline { get; set; }
    }
}
