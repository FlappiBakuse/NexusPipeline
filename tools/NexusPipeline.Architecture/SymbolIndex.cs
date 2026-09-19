using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NexusPipeline.Architecture;

public sealed record DeclarationFact(
    string FilePath,
    string Owner,
    string SymbolId,
    string Name,
    string Kind,
    int Line,
    bool IsNested,
    bool IsPartial);

public sealed record SyntaxModelFact(
    SyntaxTree Tree,
    SemanticModel Model,
    string FilePath,
    string Owner);

public static class SymbolIndex
{
    public static IReadOnlyList<SyntaxModelFact> BuildModels(CompilationFacts facts, FileOwnerResolver owners)
    {
        return facts.Compilation.SyntaxTrees
            .Select(tree => new SyntaxModelFact(
                tree,
                facts.Compilation.GetSemanticModel(tree, ignoreAccessibility: true),
                Path.GetRelativePath(facts.Root, tree.FilePath).Replace('\\', '/'),
                owners.Resolve(tree.FilePath)))
            .OrderBy(model => model.FilePath, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<DeclarationFact> CollectDeclarations(
        IReadOnlyList<SyntaxModelFact> models)
    {
        var declarations = new List<DeclarationFact>();
        foreach (var model in models)
        {
            var root = model.Tree.GetRoot();
            foreach (var node in root.DescendantNodes().Where(IsDeclarationNode))
            {
                var symbol = model.Model.GetDeclaredSymbol(node);
                if (symbol is null) continue;
                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                declarations.Add(new DeclarationFact(
                    model.FilePath,
                    model.Owner,
                    StableSymbolId(symbol),
                    symbol.Name,
                    GetKind(symbol),
                    line,
                    symbol.ContainingType is not null,
                    node is TypeDeclarationSyntax type && type.Modifiers.Any(SyntaxKind.PartialKeyword)));
            }
        }

        return declarations
            .OrderBy(declaration => declaration.FilePath, StringComparer.Ordinal)
            .ThenBy(declaration => declaration.Line)
            .ThenBy(declaration => declaration.SymbolId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsDeclarationNode(SyntaxNode node) => node is BaseTypeDeclarationSyntax
        or DelegateDeclarationSyntax
        or EnumMemberDeclarationSyntax;

    private static string GetKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol named when named.TypeKind == TypeKind.Class => "class",
        INamedTypeSymbol named when named.TypeKind == TypeKind.Struct => "struct",
        INamedTypeSymbol named when named.TypeKind == TypeKind.Interface => "interface",
        INamedTypeSymbol named when named.TypeKind == TypeKind.Enum => "enum",
        INamedTypeSymbol named when named.TypeKind == TypeKind.Delegate => "delegate",
        IFieldSymbol => "field",
        _ => symbol.Kind.ToString().ToLowerInvariant(),
    };

    private static string StableSymbolId(ISymbol symbol)
    {
        if (symbol is IFieldSymbol field && field.ContainingType is not null)
        {
            return $"{field.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{field.Name}";
        }
        return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
}
