using System.Text.Json;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace NexusPipeline.Architecture;

internal static class Program
{
    private sealed record Violation(string RuleId, string Mode, string File, int Line, string Target);
    private sealed record Edge(string From, string To);
    private sealed record ReportEdge(string Mode, string From, string To);

    private static async Task<int> Main(string[] args)
    {
        var modes = new[] { "production", "test-host" };
        var modeIndex = Array.IndexOf(args, "--mode");
        if (modeIndex >= 0)
        {
            if (modeIndex != args.Length - 2 || args[modeIndex + 1] is not ("production" or "both"))
            {
                Console.Error.WriteLine("Invalid architecture mode: use production or both");
                return 2;
            }
            if (args[modeIndex + 1] == "production") modes = ["production"];
            args = args[..modeIndex];
        }
        if (args.Length is not (3 or 7) || args[1] != "--report"
            || args.Length == 7 && (args[3] != "--frontend-props" || args[5] != "--build-identity"))
        {
            Console.Error.WriteLine("Usage: NexusPipeline.Architecture <Host root> --report <external JSON path> [--frontend-props <frozen props> --build-identity <frozen identity>] [--mode production|both]");
            return 2;
        }
        var root = Path.GetFullPath(args[0]);
        var report = Path.GetFullPath(args[2]);
        MSBuildLocator.RegisterDefaults();
        var violations = new List<Violation>();
        var reportEdges = new List<ReportEdge>();
        var sourceFingerprints = new Dictionary<string, string>();
        foreach (var mode in modes)
        {
            var properties = new Dictionary<string, string>
            {
                ["Configuration"] = "Release",
                ["RuntimeIdentifier"] = "win-x64",
                ["NexusTestHost"] = mode == "test-host" ? "true" : "false",
            };
            if (args.Length == 7)
            {
                properties["NexusFrontendProps"] = Path.GetFullPath(args[4]);
                properties["NexusBuildIdentityPath"] = Path.GetFullPath(args[6]);
            }
            using var workspace = MSBuildWorkspace.Create(properties);
            var project = await workspace.OpenProjectAsync(Path.Combine(root, "src", "NexusPipeline.csproj"));
            var compilation = await project.GetCompilationAsync()
                ?? throw new InvalidOperationException($"Compilation unavailable: {mode}");
            var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Take(20).ToArray();
            if (errors.Length > 0)
                throw new InvalidOperationException($"Compilation errors prevent architecture analysis: {mode}; references={compilation.References.Count()}; workspace={string.Join(" | ", workspace.Diagnostics.Take(5).Select(diagnostic => diagnostic.Message))}: "
                    + string.Join(" | ", errors.Select(error => error.ToString())));
            var edges = new HashSet<Edge>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                var file = tree.FilePath;
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) || !relative.StartsWith("src/", StringComparison.Ordinal))
                    continue;
                var sourceOwner = Owner(relative);
                if (sourceOwner is null) continue;
                var semantic = compilation.GetSemanticModel(tree);
                var syntax = await tree.GetRootAsync();
                foreach (var name in syntax.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    if (name.Ancestors().Any(node => node is UsingDirectiveSyntax)
                        || name.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Any(declaration => declaration.Name.Span.Contains(name.Span))) continue;
                    var symbol = semantic.GetSymbolInfo(name).Symbol;
                    if (symbol is IAliasSymbol alias) symbol = alias.Target;
                    if (symbol is null) continue;
                    var targetSymbol = symbol is ITypeSymbol ? symbol : symbol.ContainingType;
                    if (targetSymbol is null) continue;
                    var sourceLocation = targetSymbol.Locations.FirstOrDefault(location => location.IsInSource);
                    var targetOwner = sourceLocation?.SourceTree is { } targetTree
                        ? Owner(Path.GetRelativePath(root, targetTree.FilePath).Replace('\\', '/')) : null;
                    var line = name.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    if (sourceOwner != "Host" && sourceOwner != "Composition"
                        && targetSymbol.ToDisplayString() is "System.IServiceProvider" or "Microsoft.Extensions.DependencyInjection.IServiceCollection")
                        violations.Add(new Violation("A03", mode, relative, line, targetSymbol.ToDisplayString()));
                    if (targetOwner is null || sourceOwner == targetOwner) continue;
                    edges.Add(new Edge(sourceOwner, targetOwner));
                    if (sourceOwner.StartsWith("Modules.", StringComparison.Ordinal)
                        && targetOwner is "Host" or "Composition" or "ControlPlane"
                        || sourceOwner is "Platform" or "Shared" && targetOwner.StartsWith("Modules.", StringComparison.Ordinal))
                        violations.Add(new Violation("A01", mode, relative, line, targetSymbol.ToDisplayString()));
                    if (sourceOwner == "ControlPlane" && targetOwner.StartsWith("Modules.", StringComparison.Ordinal)
                        && (targetSymbol.Name.EndsWith("Store", StringComparison.Ordinal)
                            || targetSymbol.Name.EndsWith("Repository", StringComparison.Ordinal)
                            || targetSymbol.Name.EndsWith("FileService", StringComparison.Ordinal)))
                        violations.Add(new Violation("A04", mode, relative, line, targetSymbol.ToDisplayString()));
                }
            }
            foreach (var component in Cycles(edges))
                violations.Add(new Violation("A02", mode, "src/Modules", 0, string.Join(" -> ", component)));
            reportEdges.AddRange(edges.Select(edge => new ReportEdge(mode, edge.From, edge.To)));
            var input = string.Join("\n", compilation.SyntaxTrees
                .Where(tree => Path.GetRelativePath(root, tree.FilePath).Replace('\\', '/').StartsWith("src/", StringComparison.Ordinal))
                .Select(tree => Path.GetRelativePath(root, tree.FilePath).Replace('\\', '/') + "\0" + tree.GetText())
                .Order(StringComparer.Ordinal));
            sourceFingerprints[mode] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var distinct = violations.Distinct().OrderBy(item => item.RuleId).ThenBy(item => item.Mode)
            .ThenBy(item => item.File).ThenBy(item => item.Line).ToArray();
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, status = distinct.Length == 0 ? "PASS" : "FAIL",
            checkedRules = new[] { "A01", "A02", "A03", "A04" },
            modes, violations = distinct,
            edges = reportEdges.Distinct().OrderBy(edge => edge.Mode).ThenBy(edge => edge.From).ThenBy(edge => edge.To),
            sourceFingerprints,
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var violation in distinct.Take(50))
            Console.Error.WriteLine($"{violation.RuleId} {violation.Mode} {violation.File}:{violation.Line} {violation.Target}");
        return distinct.Length == 0 ? 0 : 1;
    }

    private static string? Owner(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length < 3 || parts[0] != "src") return null;
        if (parts[1] == "Modules" && parts.Length > 3) return "Modules." + parts[2];
        if (parts[1] == "Host" && parts.Length > 3 && parts[2] == "Composition") return "Composition";
        return parts[1] is "Host" or "ControlPlane" or "Platform" or "Shared" ? parts[1] : null;
    }

    private static IEnumerable<string[]> Cycles(HashSet<Edge> edges)
    {
        var nodes = edges.SelectMany(edge => new[] { edge.From, edge.To })
            .Where(node => node.StartsWith("Modules.", StringComparison.Ordinal)).Distinct().ToArray();
        var graph = nodes.ToDictionary(node => node,
            node => edges.Where(edge => edge.From == node && edge.To.StartsWith("Modules.", StringComparison.Ordinal))
                .Select(edge => edge.To).Distinct().ToArray());
        var indices = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var found = new List<string[]>();
        var next = 0;
        void Visit(string node)
        {
            indices[node] = next; low[node] = next++; stack.Push(node); onStack.Add(node);
            foreach (var target in graph[node])
            {
                if (!indices.ContainsKey(target)) { Visit(target); low[node] = Math.Min(low[node], low[target]); }
                else if (onStack.Contains(target)) low[node] = Math.Min(low[node], indices[target]);
            }
            if (low[node] != indices[node]) return;
            var component = new List<string>();
            string member;
            do { member = stack.Pop(); onStack.Remove(member); component.Add(member); } while (member != node);
            if (component.Count > 1 || graph[node].Contains(node)) found.Add(component.OrderBy(value => value).ToArray());
        }
        foreach (var node in nodes) if (!indices.ContainsKey(node)) Visit(node);
        return found;
    }
}
