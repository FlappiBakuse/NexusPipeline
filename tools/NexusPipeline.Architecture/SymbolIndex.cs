using System.Collections.Immutable;
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
    bool IsPartial)
{
    public string Mode { get; init; } = "production";
    public string ProjectKind { get; init; } = "host";
}

public sealed record SyntaxModelFact(
    SyntaxTree Tree,
    SemanticModel Model,
    string FilePath,
    string Owner)
{
    public string Mode { get; init; } = "production";
    public string ProjectKind { get; init; } = "host";
}

public static class SymbolIndex
{
    public static IReadOnlyList<SyntaxModelFact> BuildModels(CompilationFacts facts, FileOwnerResolver owners)
    {
        return facts.Compilation.SyntaxTrees
            .Where(tree => !IsGeneratedOrIntermediate(tree.FilePath, facts.Root))
            .Select(tree => new SyntaxModelFact(
                tree,
                facts.Compilation.GetSemanticModel(tree, ignoreAccessibility: true),
                Path.GetRelativePath(facts.Root, tree.FilePath).Replace('\\', '/'),
                owners.Resolve(tree.FilePath))
            {
                Mode = facts.Mode,
                ProjectKind = facts.ProjectKind,
            })
            .OrderBy(model => model.FilePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsGeneratedOrIntermediate(string filePath, string root)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return true;
        var absolute = Path.GetFullPath(filePath);
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return true;
        var relative = Path.GetRelativePath(root, filePath).Replace('\\', '/');
        return relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("src/obj/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("src/bin/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/.generated/", StringComparison.OrdinalIgnoreCase);
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
                    IsPartial(node, symbol))
                {
                    Mode = model.Mode,
                    ProjectKind = model.ProjectKind,
                });
            }
        }

        return declarations
            .GroupBy(declaration => (declaration.ProjectKind, declaration.Mode, declaration.FilePath, declaration.Line, declaration.SymbolId))
            .Select(group => group.First())
            .OrderBy(declaration => declaration.ProjectKind, StringComparer.Ordinal)
            .ThenBy(declaration => declaration.Mode, StringComparer.Ordinal)
            .ThenBy(declaration => declaration.FilePath, StringComparer.Ordinal)
            .ThenBy(declaration => declaration.Line)
            .ThenBy(declaration => declaration.SymbolId, StringComparer.Ordinal)
            .ToArray();
    }

    public static ISymbol? GetEnclosingSymbol(SyntaxModelFact model, SyntaxNode node)
        => model.Model.GetEnclosingSymbol(node.SpanStart)
            ?? model.Model.GetDeclaredSymbol(node);

    public static string StableSymbolId(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol method:
                method = method.OriginalDefinition;
                var methodOwner = method.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "global";
                var methodName = method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
                    ? ".ctor"
                    : method.Name;
                var methodArity = method.Arity == 0 ? string.Empty : $"`{method.Arity}";
                return $"{methodOwner}.{methodName}{methodArity}({ParameterSignature(method.Parameters)})";
            case IPropertySymbol property:
                property = property.OriginalDefinition;
                return $"{ContainingTypeName(property)}.{property.Name}{ParameterList(property.Parameters)}";
            case IEventSymbol @event:
                return $"{ContainingTypeName(@event)}.{@event.Name}";
            case IFieldSymbol field:
                return $"{ContainingTypeName(field)}.{field.Name}";
            case INamedTypeSymbol named:
                return named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            default:
                return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
    }

    private static string ContainingTypeName(ISymbol symbol)
        => symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "global";

    private static string ParameterList(ImmutableArray<IParameterSymbol> parameters)
        => parameters.IsDefaultOrEmpty ? string.Empty : $"({ParameterSignature(parameters)})";

    private static string ParameterSignature(IEnumerable<IParameterSymbol> parameters)
        => string.Join(",", parameters.Select(parameter =>
        {
            var refKind = parameter.RefKind == RefKind.None ? string.Empty : $"{parameter.RefKind}:";
            return $"{refKind}{parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";
        }));

    private static bool IsDeclarationNode(SyntaxNode node)
        => node is BaseTypeDeclarationSyntax
            or DelegateDeclarationSyntax
            or EnumMemberDeclarationSyntax
            or MethodDeclarationSyntax
            or ConstructorDeclarationSyntax
            or DestructorDeclarationSyntax
            or OperatorDeclarationSyntax
            or ConversionOperatorDeclarationSyntax
            or PropertyDeclarationSyntax
            or IndexerDeclarationSyntax
            or EventDeclarationSyntax
            or LocalFunctionStatementSyntax
            || node is VariableDeclaratorSyntax variable
                && variable.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax;

    private static string GetKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol named when named.TypeKind == TypeKind.Class => "class",
        INamedTypeSymbol named when named.TypeKind == TypeKind.Struct => "struct",
        INamedTypeSymbol named when named.TypeKind == TypeKind.Interface => "interface",
        INamedTypeSymbol named when named.TypeKind == TypeKind.Enum => "enum",
        INamedTypeSymbol named when named.TypeKind == TypeKind.Delegate => "delegate",
        IMethodSymbol method when method.MethodKind == MethodKind.Constructor => "constructor",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        IFieldSymbol => "field",
        _ => symbol.Kind.ToString().ToLowerInvariant(),
    };

    private static bool IsPartial(SyntaxNode node, ISymbol symbol)
    {
        if (node is BaseTypeDeclarationSyntax type && type.Modifiers.Any(SyntaxKind.PartialKeyword)) return true;
        if (symbol is INamedTypeSymbol named) return named.DeclaringSyntaxReferences.Length > 1;
        return false;
    }
}
