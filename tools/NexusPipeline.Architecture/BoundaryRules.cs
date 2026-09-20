using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NexusPipeline.Architecture;

public sealed record ArchitectureViolation(
    string RuleId,
    string StableFileId,
    string OriginalSymbolId,
    string TargetSymbolId,
    string NormalizedSyntaxHash,
    int OccurrenceOrdinal,
    string Message,
    string? FilePath,
    int? Line)
{
    public string Mode { get; init; } = "production";
    public string ProjectKind { get; init; } = "host";
}

public static class BoundaryRules
{
    private static readonly HashSet<string> Modules = new(StringComparer.Ordinal)
    {
        "Settings", "Plugins", "Scripts", "Users", "Queues", "Configuration", "History",
        "Notifications", "Execution", "Scheduling", "Updates", "Diagnostics",
    };

    private static readonly HashSet<string> ControlPlaneFileTypes = new(StringComparer.Ordinal)
    {
        "File", "Directory", "FileInfo", "DirectoryInfo", "FileStream", "StreamReader", "StreamWriter",
    };

    public static IReadOnlyList<ArchitectureViolation> Evaluate(
        IReadOnlyList<SyntaxModelFact> models,
        IReadOnlyList<DeclarationFact> declarations,
        IReadOnlyList<DependencyEdge> edges,
        FileOwnerResolver owners)
    {
        var violations = new List<ArchitectureViolation>();
        var counts = new Dictionary<(string Mode, string Rule, string File, string Symbol, string Target, string Hash), int>();

        void Add(
            string rule,
            SyntaxModelFact model,
            SyntaxNode? node,
            string symbol,
            string target,
            string message,
            string? syntax = null)
        {
            var file = model.FilePath;
            var line = node is null ? (int?)null : node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(syntax ?? message))));
            var key = (model.Mode, rule, file, symbol, target, hash);
            counts.TryGetValue(key, out var ordinal);
            counts[key] = ordinal + 1;
            violations.Add(new ArchitectureViolation(
                rule,
                file,
                symbol,
                target,
                hash,
                ordinal,
                message,
                file,
                line)
            {
                Mode = model.Mode,
                ProjectKind = model.ProjectKind,
            });
        }

        var declarationGroups = declarations
            .GroupBy(declaration => (declaration.Mode, declaration.ProjectKind, declaration.SymbolId));
        foreach (var duplicate in declarationGroups.Where(group => group.Count() > 1))
        {
            if (duplicate.All(declaration => declaration.IsPartial)
                && duplicate.Select(declaration => declaration.Owner).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                continue;
            }
            foreach (var declaration in duplicate)
            {
                var model = models.First(item => item.FilePath == declaration.FilePath && item.Mode == declaration.Mode && item.ProjectKind == declaration.ProjectKind);
                Add(
                    "A001",
                    model,
                    FindNode(model, declaration.Line),
                    declaration.SymbolId,
                    duplicate.Key.SymbolId,
                    $"Symbol is declared more than once without a valid same-owner partial declaration: {duplicate.Key.SymbolId}");
            }
        }

        foreach (var model in models)
        {
            if (model.Owner == "Unassigned")
            {
                Add("A010", model, null, $"<file:{model.FilePath}>", model.FilePath, "Source file has no owner.");
            }

            ValidateNamespaces(model, owners, Add);
            ValidateLocators(model, Add);
            if (model.Owner == "ControlPlane") ValidateControlPlaneAccess(model, Add);
        }

        foreach (var edge in edges.Where(edge => !IsTestProject(edge.ProjectKind) && !edge.IsExternal))
        {
            var model = models.FirstOrDefault(item => item.FilePath == edge.SourceFile && item.Mode == edge.Mode && item.ProjectKind == edge.ProjectKind);
            if (model is null) continue;
            if (IsModule(edge.SourceOwner) && (edge.TargetOwner is "Host" or "ControlPlane"))
            {
                Add("A004", model, FindNode(model, edge.Line), edge.SourceSymbol, edge.TargetSymbol,
                    $"{edge.SourceOwner} depends on {edge.TargetOwner}.", edge.TargetSymbol);
            }
            if (edge.SourceOwner == "Shared" && (edge.TargetOwner == "Platform" || IsModule(edge.TargetOwner)))
            {
                Add("A005", model, FindNode(model, edge.Line), edge.SourceSymbol, edge.TargetSymbol,
                    "Shared depends on Platform or a business module.", edge.TargetSymbol);
            }
            if (edge.SourceOwner == "Platform" && (IsModule(edge.TargetOwner) || edge.TargetOwner is "Host" or "ControlPlane"))
            {
                Add("A006", model, FindNode(model, edge.Line), edge.SourceSymbol, edge.TargetSymbol,
                    "Platform depends on a business or host layer.", edge.TargetSymbol);
            }
            if (edge.SourceOwner == "ControlPlane" && IsConcreteStore(edge.TargetSymbol))
            {
                Add("A007", model, FindNode(model, edge.Line), edge.SourceSymbol, edge.TargetSymbol,
                    "ControlPlane directly depends on a concrete business store; use an application port.", edge.TargetSymbol);
            }
        }

        foreach (var modeEdges in edges
            .Where(edge => !IsTestProject(edge.ProjectKind))
            .GroupBy(edge => edge.Mode, StringComparer.Ordinal))
        {
            foreach (var cycle in FindStronglyConnectedComponents(modeEdges))
            {
                if (cycle.Count < 2) continue;
                foreach (var edge in modeEdges.Where(edge => cycle.Contains(edge.SourceOwner)
                    && cycle.Contains(edge.TargetOwner)
                    && edge.SourceOwner != edge.TargetOwner))
                {
                    var model = models.FirstOrDefault(item => item.FilePath == edge.SourceFile && item.Mode == edge.Mode && item.ProjectKind == edge.ProjectKind);
                    if (model is null) continue;
                    Add("A008", model, FindNode(model, edge.Line), edge.SourceSymbol, edge.TargetSymbol,
                        $"Module dependency cycle: {string.Join(" -> ", cycle.OrderBy(item => item, StringComparer.Ordinal))}", edge.TargetSymbol);
                }
            }
        }

        return violations
            .OrderBy(violation => violation.Mode, StringComparer.Ordinal)
            .ThenBy(violation => violation.RuleId, StringComparer.Ordinal)
            .ThenBy(violation => violation.StableFileId, StringComparer.Ordinal)
            .ThenBy(violation => violation.OriginalSymbolId, StringComparer.Ordinal)
            .ThenBy(violation => violation.OccurrenceOrdinal)
            .ToArray();
    }

    public static IReadOnlyList<IReadOnlySet<string>> FindStronglyConnectedComponents(IEnumerable<DependencyEdge> edges)
    {
        var graph = new Dictionary<(string Mode, string Owner), HashSet<(string Mode, string Owner)>>();
        foreach (var edge in edges.Where(edge => IsModule(edge.SourceOwner) && IsModule(edge.TargetOwner)))
        {
            var source = (edge.Mode, edge.SourceOwner);
            var target = (edge.Mode, edge.TargetOwner);
            if (!graph.TryGetValue(source, out var targets))
            {
                targets = new HashSet<(string Mode, string Owner)>();
                graph[source] = targets;
            }
            targets.Add(target);
            graph.TryAdd(target, new HashSet<(string Mode, string Owner)>());
        }

        var index = 0;
        var stack = new Stack<(string Mode, string Owner)>();
        var onStack = new HashSet<(string Mode, string Owner)>();
        var indices = new Dictionary<(string Mode, string Owner), int>();
        var lowLinks = new Dictionary<(string Mode, string Owner), int>();
        var components = new List<IReadOnlySet<string>>();

        void Visit((string Mode, string Owner) node)
        {
            indices[node] = index;
            lowLinks[node] = index++;
            stack.Push(node);
            onStack.Add(node);
            foreach (var target in graph[node]
                .OrderBy(item => item.Mode, StringComparer.Ordinal)
                .ThenBy(item => item.Owner, StringComparer.Ordinal))
            {
                if (!indices.ContainsKey(target))
                {
                    Visit(target);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[target]);
                }
                else if (onStack.Contains(target))
                {
                    lowLinks[node] = Math.Min(lowLinks[node], indices[target]);
                }
            }

            if (lowLinks[node] != indices[node]) return;
            var component = new HashSet<string>(StringComparer.Ordinal);
            (string Mode, string Owner) value;
            do
            {
                value = stack.Pop();
                onStack.Remove(value);
                component.Add(value.Owner);
            } while (value != node);
            components.Add(component);
        }

        foreach (var node in graph.Keys
            .OrderBy(item => item.Mode, StringComparer.Ordinal)
            .ThenBy(item => item.Owner, StringComparer.Ordinal))
        {
            if (!indices.ContainsKey(node)) Visit(node);
        }
        return components;
    }

    private static void ValidateNamespaces(
        SyntaxModelFact model,
        FileOwnerResolver owners,
        Action<string, SyntaxModelFact, SyntaxNode?, string, string, string, string?> add)
    {
        if (model.FilePath.EndsWith("/GlobalUsings.cs", StringComparison.OrdinalIgnoreCase)) return;
        if (model.FilePath.EndsWith("/AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)) return;
        var root = model.Tree.GetRoot();
        var namespaces = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().ToArray();
        var expected = ExpectedNamespace(model.FilePath);
        if (expected is null)
        {
            add("A010", model, null, $"<file:{model.FilePath}>", model.Owner, "Source path cannot be mapped to a namespace owner.", model.FilePath);
            return;
        }
        if (namespaces.Length == 0)
        {
            add("A002", model, root, $"<file:{model.FilePath}>", expected, $"Missing namespace; expected {expected}.", expected);
            return;
        }
        foreach (var namespaceNode in namespaces)
        {
            var declared = model.Model.GetDeclaredSymbol(namespaceNode)?.ToDisplayString();
            if (!string.Equals(declared, expected, StringComparison.Ordinal))
            {
                add("A002", model, namespaceNode, declared ?? namespaceNode.Name.ToString(), expected,
                    $"Namespace must equal the directory-derived namespace {expected}; actual {declared ?? "<unresolved>"}.", declared ?? namespaceNode.Name.ToString());
            }
        }
    }

    private static void ValidateLocators(
        SyntaxModelFact model,
        Action<string, SyntaxModelFact, SyntaxNode?, string, string, string, string?> add)
    {
        // Tests may resolve the explicit composition fixture to exercise a real
        // host graph. Locator rules constrain production/control-plane code;
        // test calls are intentionally outside the product dependency policy.
        if (IsTestProject(model.ProjectKind)) return;
        if (model.FilePath.StartsWith("src/Host/Composition/", StringComparison.Ordinal)
            || model.FilePath.Equals("src/Host/Composition", StringComparison.Ordinal)) return;
        foreach (var node in model.Tree.GetRoot().DescendantNodes())
        {
            if (!IsLocator(model, node)) continue;
            var symbol = SymbolIndex.GetEnclosingSymbol(model, node);
            add("A003", model, node, symbol is null ? $"<file:{model.FilePath}>" : SymbolIndex.StableSymbolId(symbol), node.ToString(),
                "Business or control-plane code retains a service locator, global root, or IServiceProvider access.", node.ToString());
        }
    }

    private static bool IsLocator(SyntaxModelFact model, SyntaxNode node)
    {
        if (node is TypeSyntax)
        {
            var type = model.Model.GetTypeInfo(node).Type;
            if (type is not null && type.Name is "IServiceProvider" or "ServiceProvider" or "RuntimeContext") return true;
        }
        if (node is InvocationExpressionSyntax invocation)
        {
            var method = model.Model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (method is null) return false;
            if (method.Name is "GetService" or "GetRequiredService")
            {
                return method.ContainingType?.Name is "IServiceProvider" or "ServiceProvider"
                    || method.IsExtensionMethod && method.ContainingNamespace?.ToDisplayString().StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal) == true;
            }
            if (method.Name == "Resolve")
            {
                var receiver = invocation.Expression is MemberAccessExpressionSyntax member
                    ? model.Model.GetTypeInfo(member.Expression).Type
                    : null;
                return receiver?.Name is "HostCompositionRoot" or "RuntimeContext";
            }
        }
        if (node is MemberAccessExpressionSyntax access)
        {
            var symbol = model.Model.GetSymbolInfo(access).Symbol;
            // Directly using the composition root's typed factory/property is part of
            // composition; only its locator-shaped members are forbidden here.
            if (symbol?.ContainingType?.Name is "HostCompositionRoot" or "RuntimeContext"
                && symbol.Name is "Resolve" or "GetService" or "GetRequiredService") return true;
            if (access.Name.Identifier.ValueText == "Instance")
            {
                var type = model.Model.GetTypeInfo(access.Expression).Type;
                return type?.Name is "HostCompositionRoot" or "RuntimeContext";
            }
        }
        return false;
    }

    private static void ValidateControlPlaneAccess(
        SyntaxModelFact model,
        Action<string, SyntaxModelFact, SyntaxNode?, string, string, string, string?> add)
    {
        foreach (var node in model.Tree.GetRoot().DescendantNodes())
        {
            var targetType = node switch
            {
                InvocationExpressionSyntax invocation => (model.Model.GetSymbolInfo(invocation).Symbol as IMethodSymbol)?.ContainingType,
                ObjectCreationExpressionSyntax creation => model.Model.GetTypeInfo(creation).Type,
                AttributeSyntax attribute => model.Model.GetSymbolInfo(attribute).Symbol?.ContainingType,
                _ => null,
            };
            if (targetType is not null && targetType.ContainingNamespace?.ToDisplayString() == "System.IO"
                && ControlPlaneFileTypes.Contains(targetType.Name))
            {
                var symbol = SymbolIndex.GetEnclosingSymbol(model, node);
                add("A007", model, node, symbol is null ? $"<file:{model.FilePath}>" : SymbolIndex.StableSymbolId(symbol), targetType.ToDisplayString(),
                    "ControlPlane directly accesses a filesystem type; use a platform/application port.", targetType.ToDisplayString());
            }
            if (node is AttributeSyntax && model.Model.GetSymbolInfo(node).Symbol?.Name is "DllImportAttribute" or "LibraryImportAttribute")
            {
                var symbol = SymbolIndex.GetEnclosingSymbol(model, node);
                add("A007", model, node, symbol is null ? $"<file:{model.FilePath}>" : SymbolIndex.StableSymbolId(symbol), node.ToString(),
                    "ControlPlane directly declares a native API import.", node.ToString());
            }
        }
    }

    private static bool IsConcreteStore(string targetSymbol)
    {
        var name = targetSymbol.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        return (name.EndsWith("Store", StringComparison.Ordinal)
            || name.EndsWith("Repository", StringComparison.Ordinal)
            || name.EndsWith("Transaction", StringComparison.Ordinal))
            && !name.StartsWith('I');
    }

    private static bool IsModule(string owner) => Modules.Contains(owner);

    private static bool IsTestProject(string projectKind)
        => projectKind.Equals("tests", StringComparison.OrdinalIgnoreCase)
            || projectKind.StartsWith("test-", StringComparison.OrdinalIgnoreCase);

    private static string? ExpectedNamespace(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (parts[0] == "src")
        {
            if (parts[1] == "NexusPipeline.Plugin.Abstractions")
            {
                return string.Join('.', new[] { "NexusPipeline", "Plugin", "Abstractions" }.Concat(parts.Skip(2).SkipLast(1)));
            }
            return string.Join('.', new[] { "NexusPipeline" }.Concat(parts.Skip(1).SkipLast(1)));
        }
        if (parts[0] == "tests" && parts.Length >= 3 && parts[1] == "NexusPipeline.Tests")
        {
            return string.Join('.', new[] { "NexusPipeline", "Tests" }.Concat(parts.Skip(2).SkipLast(1)));
        }
        if (parts[0] == "tests" && parts.Length >= 4 && parts[1] == "fixtures")
        {
            return string.Join('.', new[] { parts[2] }.Concat(parts.Skip(3).SkipLast(1)));
        }
        return null;
    }

    private static SyntaxNode? FindNode(SyntaxModelFact model, int line)
        => model.Tree.GetRoot().DescendantNodesAndSelf()
            .FirstOrDefault(node => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line);

    private static string Normalize(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
