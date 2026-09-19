using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NexusPipeline.Architecture;

public sealed record DependencyEdge(
    string SourceFile,
    string SourceOwner,
    string SourceSymbol,
    string TargetSymbol,
    string TargetOwner,
    string Kind,
    int Line);

public static class DependencyCollector
{
    public static IReadOnlyList<DependencyEdge> Collect(
        IReadOnlyList<SyntaxModelFact> models,
        IReadOnlyList<DeclarationFact> declarations)
    {
        var declarationBySymbol = declarations
            .GroupBy(declaration => declaration.SymbolId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var edges = new List<DependencyEdge>();

        foreach (var model in models)
        {
            var root = model.Tree.GetRoot();
            var sourceDeclarations = root.DescendantNodes()
                .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                .Select(node => model.Model.GetDeclaredSymbol(node))
                .Where(symbol => symbol is not null)
                .Cast<ISymbol>()
                .ToArray();
            var sourceSymbol = sourceDeclarations.FirstOrDefault()?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                ?? model.FilePath;

            foreach (var node in root.DescendantNodes())
            {
                ISymbol? target = node switch
                {
                    Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation => model.Model.GetSymbolInfo(invocation).Symbol,
                    Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax creation => model.Model.GetSymbolInfo(creation).Symbol,
                    Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax identifier => model.Model.GetSymbolInfo(identifier).Symbol,
                    Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qualified => model.Model.GetSymbolInfo(qualified).Symbol,
                    Microsoft.CodeAnalysis.CSharp.Syntax.SimpleBaseTypeSyntax baseType => model.Model.GetTypeInfo(baseType.Type).Type,
                    Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax attribute => model.Model.GetSymbolInfo(attribute).Symbol,
                    _ => null,
                };
                if (target is null) continue;
                var named = target switch
                {
                    IMethodSymbol method => method.ContainingType,
                    IPropertySymbol property => property.ContainingType,
                    IFieldSymbol field => field.ContainingType,
                    IEventSymbol @event => @event.ContainingType,
                    ITypeSymbol type => type,
                    _ => target.ContainingType,
                };
                if (named is null || named.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true) continue;
                var targetSymbol = named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (!declarationBySymbol.TryGetValue(targetSymbol, out var targetDeclaration)) continue;
                if (string.Equals(model.Owner, targetDeclaration.Owner, StringComparison.Ordinal)) continue;
                edges.Add(new DependencyEdge(
                    model.FilePath,
                    model.Owner,
                    sourceSymbol,
                    targetSymbol,
                    targetDeclaration.Owner,
                    node.Kind().ToString(),
                    node.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }
        }

        return edges
            .DistinctBy(edge => (edge.SourceFile, edge.SourceSymbol, edge.TargetSymbol, edge.Kind, edge.Line))
            .OrderBy(edge => edge.SourceOwner, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetOwner, StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceFile, StringComparer.Ordinal)
            .ThenBy(edge => edge.Line)
            .ToArray();
    }
}
