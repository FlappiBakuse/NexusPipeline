using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

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
    int? Line);

public static class BoundaryRules
{
    private static readonly HashSet<string> Modules = new(StringComparer.Ordinal)
    {
        "Settings", "Plugins", "Scripts", "Users", "Queues", "Configuration", "History",
        "Notifications", "Execution", "Scheduling", "Updates", "Diagnostics",
    };

    public static IReadOnlyList<ArchitectureViolation> Evaluate(
        IReadOnlyList<SyntaxModelFact> models,
        IReadOnlyList<DeclarationFact> declarations,
        IReadOnlyList<DependencyEdge> edges,
        FileOwnerResolver owners)
    {
        var violations = new List<ArchitectureViolation>();
        var counts = new Dictionary<(string Rule, string File, string Symbol, string Target, string Hash), int>();

        void Add(string rule, string file, string symbol, string target, string message, int? line, string? syntax = null)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(syntax ?? message))));
            var key = (rule, file, symbol, target, hash);
            counts.TryGetValue(key, out var ordinal);
            counts[key] = ordinal + 1;
            violations.Add(new ArchitectureViolation(rule, file, symbol, target, hash, ordinal, message, file, line));
        }

        foreach (var duplicate in declarations.GroupBy(d => d.SymbolId, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            if (duplicate.All(declaration => declaration.IsPartial))
            {
                continue;
            }
            foreach (var declaration in duplicate)
            {
                Add("A001", declaration.FilePath, declaration.SymbolId, duplicate.Key,
                    $"Symbol is declared more than once: {duplicate.Key}", declaration.Line);
            }
        }

        foreach (var model in models)
        {
            var expected = owners.Resolve(model.FilePath);
            var root = model.Tree.GetRoot();
            foreach (var namespaceNode in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax>())
            {
                var declared = namespaceNode.Name.ToString();
                if (model.FilePath.StartsWith("src/Modules/", StringComparison.Ordinal)
                    && !declared.Contains("Modules", StringComparison.Ordinal))
                {
                    Add("A002", model.FilePath, declared, expected, "Namespace does not follow the migrated module directory.", namespaceNode.GetLocation().GetLineSpan().StartLinePosition.Line + 1, declared);
                }
            }

            foreach (var member in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>())
            {
                var text = member.ToString();
                if (text.Contains("RuntimeContext.Instance", StringComparison.Ordinal)
                    || text.Contains("IServiceProvider", StringComparison.Ordinal)
                    || text.Contains(".GetService", StringComparison.Ordinal))
                {
                    if (!string.Equals(model.Owner, "Host", StringComparison.Ordinal)
                        && !model.FilePath.Contains("/Host/Composition/", StringComparison.Ordinal))
                    {
                        Add("A003", model.FilePath, model.Owner, text, "Business or control-plane code retains a service locator/runtime singleton access.", member.GetLocation().GetLineSpan().StartLinePosition.Line + 1, text);
                    }
                }
            }

            if (string.Equals(model.Owner, "ControlPlane", StringComparison.Ordinal))
            {
                foreach (var node in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>())
                {
                    if (node.Identifier.ValueText is "File" or "Directory" or "FileStream" or "DirectoryInfo" or "DllImport")
                    {
                        Add("A007", model.FilePath, model.Owner, node.Identifier.ValueText, "ControlPlane directly accesses filesystem or native APIs.", node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, node.Identifier.ValueText);
                    }
                }
            }
        }

        foreach (var edge in edges)
        {
            if (IsModule(edge.SourceOwner) && (edge.TargetOwner is "Host" or "ControlPlane"))
            {
                Add("A004", edge.SourceFile, edge.SourceSymbol, edge.TargetSymbol, $"{edge.SourceOwner} depends on {edge.TargetOwner}.", edge.Line, edge.TargetSymbol);
            }
            if (edge.SourceOwner == "Shared" && (edge.TargetOwner == "Platform" || IsModule(edge.TargetOwner)))
            {
                Add("A005", edge.SourceFile, edge.SourceSymbol, edge.TargetSymbol, "Shared depends on Platform or a business module.", edge.Line, edge.TargetSymbol);
            }
            if (edge.SourceOwner == "Platform" && (IsModule(edge.TargetOwner) || edge.TargetOwner is "Host" or "ControlPlane"))
            {
                Add("A006", edge.SourceFile, edge.SourceSymbol, edge.TargetSymbol, "Platform depends on a business or host layer.", edge.Line, edge.TargetSymbol);
            }
        }

        return violations
            .OrderBy(v => v.RuleId, StringComparer.Ordinal)
            .ThenBy(v => v.StableFileId, StringComparer.Ordinal)
            .ThenBy(v => v.OccurrenceOrdinal)
            .ToArray();
    }

    private static bool IsModule(string owner) => Modules.Contains(owner);

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
