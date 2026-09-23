using System.Text.Json;
using System.Text.Json.Serialization;

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
            if (mode is not ("production" or "test-host" or "both"))
            {
                return Usage($"Unsupported architecture mode: {mode}");
            }
            var plan = options.FilePlan is null ? null : Path.GetFullPath(options.FilePlan);
            Console.WriteLine($"[architecture] command={options.Command} mode={mode}");
            Console.WriteLine("[architecture] loading MSBuild project(s)");
            var analysis = await AnalyzeAsync(root, mode, plan, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"[architecture] files={analysis.Facts.SelectMany(fact => fact.SourceFiles).Distinct(StringComparer.Ordinal).Count()} declarations={analysis.Declarations.Count} edges={analysis.Edges.Count} violations={analysis.Violations.Count}");

            if (options.Command == "map")
            {
                var output = options.Output is null
                    ? Path.Combine(root, ".generated", "architecture", "backend-map.json")
                    : Path.GetFullPath(options.Output);
                var map = BackendMapWriter.Build(analysis.Facts, analysis.Models, analysis.Declarations, analysis.Edges, root);
                var validationErrors = BackendMapWriter.Validate(map, root, mode);
                if (validationErrors.Count > 0)
                {
                    throw new InvalidDataException("Generated backend map is invalid:" + Environment.NewLine
                        + string.Join(Environment.NewLine, validationErrors.Select(error => $"- {error}")));
                }
                if (options.CheckMap)
                {
                    if (BackendMapWriter.Matches(map, output))
                    {
                        Console.WriteLine($"[architecture] map check passed: {output}");
                        return 0;
                    }
                    Console.Error.WriteLine($"[architecture] map check failed: {output} is not the deterministic current map");
                    return 1;
                }
                BackendMapWriter.Write(map, output);
                Console.WriteLine($"[architecture] map written: {output}");
                return 0;
            }

            if (options.Command == "baseline-init")
            {
                var output = Require(options.Output, "--out");
                if (File.Exists(output)) throw new InvalidOperationException($"Baseline already exists: {output}");
                WriteAtomic(BuildReport(analysis, "ANALYSIS"), output);
                Console.WriteLine($"[architecture] baseline written: {output}");
                return 0;
            }

            if (options.Command == "baseline-prune")
            {
                var baselinePath = Require(options.Baseline, "--baseline");
                var baseline = ReadViolations(baselinePath);
                var comparison = DebtBaseline.Compare(analysis.Violations, baseline);
                Console.WriteLine($"[architecture] baseline prune comparison: removed={comparison.Removed.Count} retained={comparison.Retained.Count} added={comparison.Added.Count}");
                if (comparison.Added.Count > 0)
                {
                    PrintViolations(comparison.Added);
                    Console.Error.WriteLine("[architecture] baseline-prune refused to write because Added is non-empty");
                    return 1;
                }
                var output = options.Output is null ? baselinePath : Path.GetFullPath(options.Output);
                WriteAtomic(BuildViolationDocument(analysis, "PRUNED", comparison.Retained), output);
                Console.WriteLine($"[architecture] baseline pruned: {output}");
                return 0;
            }

            if (options.Command == "check")
            {
                DebtComparison? comparison = null;
                if (options.Baseline is not null)
                {
                    comparison = DebtBaseline.Compare(analysis.Violations, ReadViolations(Path.GetFullPath(options.Baseline)));
                    Console.WriteLine($"[architecture] check added={comparison.Added.Count} removed={comparison.Removed.Count} retained={comparison.Retained.Count}");
                    if (comparison.Added.Count > 0) PrintViolations(comparison.Added);
                }
                else
                {
                    Console.WriteLine($"[architecture] check current violations={analysis.Violations.Count} (no baseline; zero required)");
                    if (analysis.Violations.Count > 0) PrintViolations(analysis.Violations);
                }

                var passed = comparison is not null ? comparison.Added.Count == 0 : analysis.Violations.Count == 0;
                if (options.Output is not null) WriteAtomic(BuildReport(analysis, passed ? "PASS" : "FAIL", comparison), Path.GetFullPath(options.Output));
                return passed ? 0 : 1;
            }

            var status = analysis.Violations.Count == 0 ? "PASS" : "VIOLATIONS";
            if (options.Output is not null) WriteAtomic(BuildReport(analysis, status), Path.GetFullPath(options.Output));
            Console.WriteLine($"[architecture] analyze status={status}; analyze is a report command and never substitutes for check");
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

    private static async Task<AnalysisResult> AnalyzeAsync(string root, string mode, string? plan, CancellationToken cancellationToken)
    {
        using var loaded = await ProjectLoader.LoadAsync(root, mode, cancellationToken).ConfigureAwait(false);
        var facts = new List<CompilationFacts>();
        foreach (var loadedMode in loaded.Modes)
        {
            facts.AddRange(await CompilationFactsBuilder.BuildAsync(loadedMode, cancellationToken).ConfigureAwait(false));
        }

        var owners = new FileOwnerResolver(root, plan);
        var models = facts.SelectMany(fact => SymbolIndex.BuildModels(fact, owners)).ToArray();
        var declarations = SymbolIndex.CollectDeclarations(models);
        var collection = DependencyCollector.CollectDetailed(models, declarations);
        if (collection.Diagnostics.Count > 0)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                collection.Diagnostics.Select(diagnostic => $"{diagnostic.FilePath}:{diagnostic.Line}: {diagnostic.Message}")));
        }

        // Structure/locator rules must inspect both product and test-host graphs.
        // Product dependency rules are filtered inside BoundaryRules so test edges
        // cannot create production boundary debt.
        var violations = BoundaryRules.Evaluate(models, declarations, collection.Edges, owners);
        return new AnalysisResult(root, mode, facts, models, declarations, collection.Edges, violations, loaded.WorkspaceDiagnostics);
    }

    private static object BuildReport(AnalysisResult analysis, string status, DebtComparison? comparison = null)
        => new
        {
            schemaVersion = 2,
            status,
            root = analysis.Root,
            mode = analysis.Mode,
            sourceFiles = analysis.Facts.SelectMany(fact => fact.SourceFiles).Distinct(StringComparer.Ordinal).OrderBy(file => file, StringComparer.Ordinal).ToArray(),
            referenceAssemblies = analysis.Facts.SelectMany(fact => fact.ReferenceAssemblies).Distinct(StringComparer.Ordinal).OrderBy(file => file, StringComparer.Ordinal).ToArray(),
            workspaceDiagnostics = analysis.WorkspaceDiagnostics,
            declarations = analysis.Declarations,
            dependencies = analysis.Edges,
            violations = analysis.Violations,
            comparison,
        };

    private static object BuildViolationDocument(AnalysisResult analysis, string status, IReadOnlyList<ArchitectureViolation> violations)
        => new
        {
            schemaVersion = 2,
            status,
            root = analysis.Root,
            mode = analysis.Mode,
            violations,
        };

    private static IReadOnlyList<ArchitectureViolation> ReadViolations(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Architecture baseline was not found.", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Baseline root must be an object.");
        if (!TryGetProperty(document.RootElement, "schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out var schemaVersion)
            || schemaVersion is not (1 or 2))
        {
            throw new InvalidDataException("Baseline schemaVersion is invalid.");
        }
        if (!TryGetProperty(document.RootElement, "violations", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Baseline must contain a violations array.");
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var result = new List<ArchitectureViolation>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Each baseline violation must be an object.");
            foreach (var required in new[] { "ruleId", "stableFileId", "originalSymbolId", "targetSymbolId", "normalizedSyntaxHash", "occurrenceOrdinal", "message", "filePath" })
            {
                if (!TryGetProperty(value, required, out var field) || field.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    throw new InvalidDataException($"Baseline violation is missing required field: {required}");
                }
            }
            var violation = value.Deserialize<ArchitectureViolation>(options)
                ?? throw new InvalidDataException("Baseline violation could not be deserialized.");
            if (string.IsNullOrWhiteSpace(violation.RuleId)
                || string.IsNullOrWhiteSpace(violation.StableFileId)
                || string.IsNullOrWhiteSpace(violation.OriginalSymbolId)
                || string.IsNullOrWhiteSpace(violation.TargetSymbolId)
                || string.IsNullOrWhiteSpace(violation.NormalizedSyntaxHash)
                || string.IsNullOrWhiteSpace(violation.Message)
                || violation.OccurrenceOrdinal < 0
                || !IsSafeRelativePath(violation.StableFileId)
                || (violation.FilePath is not null && !IsSafeRelativePath(violation.FilePath)))
            {
                throw new InvalidDataException("Baseline violation contains invalid identity or path fields.");
            }
            var identity = DebtBaseline.IdentityKey(violation);
            if (!identities.Add(identity)) throw new InvalidDataException($"Baseline contains duplicate violation identity: {identity}");
            result.Add(violation);
        }
        return result;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool IsSafeRelativePath(string value)
    {
        var normalized = value.Replace('\\', '/');
        return !string.IsNullOrWhiteSpace(normalized)
            && !Path.IsPathRooted(normalized)
            && !normalized.StartsWith("../", StringComparison.Ordinal)
            && !normalized.Contains("/../", StringComparison.Ordinal)
            && !normalized.Equals("..", StringComparison.Ordinal)
            && !System.Text.RegularExpressions.Regex.IsMatch(normalized, "^[A-Za-z]:/");
    }

    private static void PrintViolations(IEnumerable<ArchitectureViolation> violations)
    {
        foreach (var violation in violations.Take(100))
        {
            Console.Error.WriteLine($"{violation.Mode} {violation.RuleId} {violation.StableFileId}:{violation.Line} {violation.Message}");
        }
    }

    private static void WriteAtomic(object value, string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = $"{fullPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        }) + Environment.NewLine;
        try
        {
            File.WriteAllText(temporary, json);
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Require(string? value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException($"{name} is required.")
        : Path.GetFullPath(value);

    private static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        if (args.Length == 0) return options;
        options.Command = args[0].ToLowerInvariant();
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--check")
            {
                options.CheckMap = true;
                continue;
            }
            if (!argument.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown argument: {argument}");
            if (index + 1 >= args.Length) throw new ArgumentException($"Value is missing for {argument}");
            var value = args[++index];
            switch (argument)
            {
                case "--root": options.Root = value; break;
                case "--mode": options.Mode = value.ToLowerInvariant(); break;
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
        Console.Error.WriteLine("Usage: analyze|check|map|baseline-init|baseline-prune --root <host> --mode production|test-host|both [--file-plan <json>] [--out <json>] [--baseline <json>] [--check]");
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
        public bool CheckMap { get; set; }
    }

    private sealed record AnalysisResult(
        string Root,
        string Mode,
        IReadOnlyList<CompilationFacts> Facts,
        IReadOnlyList<SyntaxModelFact> Models,
        IReadOnlyList<DeclarationFact> Declarations,
        IReadOnlyList<DependencyEdge> Edges,
        IReadOnlyList<ArchitectureViolation> Violations,
        IReadOnlyList<WorkspaceDiagnostic> WorkspaceDiagnostics);
}
