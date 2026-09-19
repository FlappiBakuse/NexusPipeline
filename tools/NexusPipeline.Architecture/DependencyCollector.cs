using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NexusPipeline.Architecture;

public sealed record DependencyDiagnostic(string FilePath, int Line, string Message);

public sealed record DependencyCollectionResult(
    IReadOnlyList<DependencyEdge> Edges,
    IReadOnlyList<DependencyDiagnostic> Diagnostics);

public sealed record DependencyEdge(
    string SourceFile,
    string SourceOwner,
    string SourceSymbol,
    string TargetSymbol,
    string TargetOwner,
    string Kind,
    int Line)
{
    public string Mode { get; init; } = "production";
    public string ProjectKind { get; init; } = "host";
    public bool IsExternal => string.Equals(TargetOwner, "external", StringComparison.Ordinal);
}

public static class DependencyCollector
{
    public static IReadOnlyList<DependencyEdge> Collect(
        IReadOnlyList<SyntaxModelFact> models,
        IReadOnlyList<DeclarationFact> declarations)
    {
        var result = CollectDetailed(models, declarations);
        if (result.Diagnostics.Count > 0)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(diagnostic => $"{diagnostic.FilePath}:{diagnostic.Line}: {diagnostic.Message}")));
        }
        return result.Edges;
    }

    public static DependencyCollectionResult CollectDetailed(
        IReadOnlyList<SyntaxModelFact> models,
        IReadOnlyList<DeclarationFact> declarations)
    {
        var declarationBySymbol = declarations
            .GroupBy(declaration => declaration.SymbolId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var edges = new List<DependencyEdge>();
        var diagnostics = new List<DependencyDiagnostic>();

        foreach (var model in models)
        {
            var root = model.Tree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                if (node is UsingDirectiveSyntax usingDirective
                    && usingDirective.Alias is null
                    && usingDirective.StaticKeyword.RawKind == 0) continue;
                var source = SymbolIndex.GetEnclosingSymbol(model, node);
                if (source is INamespaceSymbol) continue;
                var sourceSymbol = source is null ? $"<file:{model.FilePath}>" : SymbolIndex.StableSymbolId(source);
                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var targetSymbols = ResolveSymbols(model, node, diagnostics, line);
                foreach (var target in targetSymbols)
                {
                    foreach (var normalized in ExpandTarget(target))
                    {
                        if (normalized.Symbol is INamedTypeSymbol { IsAnonymousType: true }) continue;
                        var targetSymbol = SymbolIndex.StableSymbolId(normalized.Symbol);
                        if (string.IsNullOrWhiteSpace(targetSymbol)) continue;
                        if (targetSymbol is "class" or "struct" or "unmanaged" or "notnull") continue;
                        if (string.Equals(targetSymbol, sourceSymbol, StringComparison.Ordinal)) continue;
                        var targetDeclaration = declarationBySymbol.GetValueOrDefault(targetSymbol);
                        var targetOwner = targetDeclaration?.Owner
                            ?? (IsLocalNexusSymbol(normalized.Symbol) ? "Unassigned" : "external");
                        if (targetOwner == "Unassigned")
                        {
                            diagnostics.Add(new DependencyDiagnostic(
                                model.FilePath,
                                line,
                                $"Local symbol has no declaration/owner: {targetSymbol}"));
                            continue;
                        }

                        edges.Add(new DependencyEdge(
                            model.FilePath,
                            model.Owner,
                            sourceSymbol,
                            targetSymbol,
                            targetOwner,
                            node.Kind().ToString(),
                            line)
                        {
                            Mode = model.Mode,
                            ProjectKind = model.ProjectKind,
                        });
                    }
                }
            }
        }

        var distinct = edges
            .DistinctBy(edge => (
                edge.Mode,
                edge.ProjectKind,
                edge.SourceFile,
                edge.SourceSymbol,
                edge.TargetSymbol,
                edge.TargetOwner,
                edge.Kind,
                edge.Line))
            .OrderBy(edge => edge.Mode, StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceOwner, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetOwner, StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceFile, StringComparer.Ordinal)
            .ThenBy(edge => edge.Line)
            .ThenBy(edge => edge.TargetSymbol, StringComparer.Ordinal)
            .ToArray();
        return new DependencyCollectionResult(distinct, diagnostics
            .Distinct()
            .OrderBy(diagnostic => diagnostic.FilePath, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Line)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToArray());
    }

    private static IEnumerable<ISymbol> ResolveSymbols(
        SyntaxModelFact model,
        SyntaxNode node,
        ICollection<DependencyDiagnostic> diagnostics,
        int line)
    {
        var symbols = new List<ISymbol>();
        if (node is UsingDirectiveSyntax usingDirective)
        {
            var usingInfo = model.Model.GetSymbolInfo(usingDirective.Name!);
            if (usingInfo.Symbol is not null) symbols.Add(usingInfo.Symbol);
            if (usingInfo.CandidateReason == CandidateReason.Ambiguous)
            {
                diagnostics.Add(new DependencyDiagnostic(model.FilePath, line, $"Ambiguous using binding: {usingDirective.Name}"));
                symbols.AddRange(usingInfo.CandidateSymbols);
            }
        }
        var info = model.Model.GetSymbolInfo(node);
        if (info.Symbol is not null) symbols.Add(info.Symbol);
        if (info.CandidateReason == CandidateReason.Ambiguous)
        {
            diagnostics.Add(new DependencyDiagnostic(model.FilePath, line, $"Ambiguous symbol binding: {node}"));
            symbols.AddRange(info.CandidateSymbols);
        }

        if (node is TypeSyntax)
        {
            var typeInfo = model.Model.GetTypeInfo(node);
            if (typeInfo.Type is not null) symbols.Add(typeInfo.Type);
            if (typeInfo.ConvertedType is not null) symbols.Add(typeInfo.ConvertedType);
        }

        return symbols
            .Where(symbol => symbol is not INamespaceSymbol)
            .Distinct(SymbolEqualityComparer.Default);
    }

    private static IEnumerable<NormalizedTarget> ExpandTarget(ISymbol target)
    {
        switch (target)
        {
            case IMethodSymbol { MethodKind: MethodKind.LocalFunction }:
                yield break;
            case IMethodSymbol method:
                if (method.ContainingType is not null) yield return new NormalizedTarget(method.ContainingType);
                yield break;
            case IPropertySymbol property:
                if (property.ContainingType is not null) yield return new NormalizedTarget(property.ContainingType);
                yield break;
            case IEventSymbol @event:
                if (@event.ContainingType is not null) yield return new NormalizedTarget(@event.ContainingType);
                yield break;
            case IFieldSymbol field:
                if (field.ContainingType is not null) yield return new NormalizedTarget(field.ContainingType);
                yield break;
            case ILocalSymbol local:
                // Local variables describe expression flow, not a module dependency. Their
                // declared type is collected from the corresponding TypeSyntax node.
                yield break;
            case IParameterSymbol parameter:
                // Parameters likewise must not turn every method body into a dependency on
                // the containing method/local function symbol.
                yield break;
            case ITypeParameterSymbol:
                // Constraint types are visited as TypeSyntax nodes. Do not report the type
                // parameter itself as an unresolved project symbol.
                yield break;
            case ITypeSymbol type:
                foreach (var nested in ExpandType(type)) yield return new NormalizedTarget(nested);
                yield break;
            default:
                yield return new NormalizedTarget(target);
                yield break;
        }
    }

    private static IEnumerable<ITypeSymbol> ExpandType(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                foreach (var nested in ExpandType(array.ElementType)) yield return nested;
                yield break;
            case IPointerTypeSymbol pointer:
                foreach (var nested in ExpandType(pointer.PointedAtType)) yield return nested;
                yield break;
            case INamedTypeSymbol named:
                if (named.IsAnonymousType) yield break;
                yield return named.OriginalDefinition;
                foreach (var argument in named.TypeArguments)
                {
                    foreach (var nested in ExpandType(argument)) yield return nested;
                }
                yield break;
            case ITypeParameterSymbol parameter:
                foreach (var constraint in parameter.ConstraintTypes)
                {
                    foreach (var nested in ExpandType(constraint)) yield return nested;
                }
                yield break;
            default:
                yield return type;
                yield break;
        }
    }

    private static bool IsLocalNexusSymbol(ISymbol symbol)
        => symbol.ContainingAssembly?.Name?.StartsWith("nexus-pipeline", StringComparison.OrdinalIgnoreCase) == true
            || symbol.ContainingNamespace?.ToDisplayString().StartsWith("NexusPipeline", StringComparison.Ordinal) == true;

    private sealed record NormalizedTarget(ISymbol Symbol);
}
