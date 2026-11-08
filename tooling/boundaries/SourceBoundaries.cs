using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class SourceBoundaries
{
    internal static int Verify(string root)
    {
        root = Path.GetFullPath(root);
        var directories = new[] { "src/core", "src/platform", "sdk/dotnet", "apps/host" };
        var trees = directories.SelectMany(directory => Directory.GetFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
            .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.CSharp14), file)).ToArray();
        var common = CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;");
        var trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? throw new InvalidOperationException("framework_references_missing"))
            .Split(Path.PathSeparator).Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("ArchitectureInspection", trees.Append(common), trusted,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = new List<object>();
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var locations = new Dictionary<string, (string Source, int Line)>(StringComparer.Ordinal);
        var symbols = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                if (model.GetDeclaredSymbol(declaration) is INamedTypeSymbol type && type.ContainingNamespace.ToDisplayString() == "NexusPipeline.Core.Settings")
                {
                    var name = type.ToDisplayString(); symbols[type] = name; graph.TryAdd(name, []);
                    locations[name] = (Path.GetRelativePath(root, tree.FilePath).Replace('\\', '/'), declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                }
        }
        foreach (var tree in trees)
        {
            var source = Path.GetRelativePath(root, tree.FilePath).Replace('\\', '/');
            var model = compilation.GetSemanticModel(tree);
            var syntax = tree.GetRoot();
            if (!source.EndsWith(".g.cs", StringComparison.Ordinal) &&
                (source.StartsWith("src/", StringComparison.Ordinal) || source.StartsWith("apps/host/", StringComparison.Ordinal)))
                foreach (var comparison in syntax.DescendantNodes().OfType<BinaryExpressionSyntax>())
                    if (comparison.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression &&
                        new[] { comparison.Left, comparison.Right }.Any(value => value is LiteralExpressionSyntax literal && literal.Token.Value is string { Length: > 0 }) &&
                        new[] { comparison.Left, comparison.Right }.Any(value =>
                            value is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText is "ExtensionId" or "ProviderId" or "Vendor" ||
                            value is IdentifierNameSyntax identifier && identifier.Identifier.ValueText is "extensionId" or "providerId"))
                        Error("PLUGIN_ID_BRANCH", comparison, comparison.ToString());
            foreach (var invocation in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var method = invocation.Expression switch
                { MemberAccessExpressionSyntax member => member.Name, SimpleNameSyntax simple => simple, _ => null };
                var name = method?.Identifier.ValueText;
                if (name is "GetService" or "GetRequiredService" or "GetServices")
                {
                    var logging = source == "apps/host/Control/Http/ControlExceptionBoundary.cs" && method is GenericNameSyntax generic &&
                        generic.TypeArgumentList.Arguments.Count == 1 && generic.TypeArgumentList.Arguments[0].ToString() == "ILoggerFactory";
                    if (!logging && !source.StartsWith("apps/host/Composition/", StringComparison.Ordinal))
                        Error("NO_SERVICE_LOCATOR", invocation, name);
                }
                if (source.StartsWith("apps/host/Control/Http/", StringComparison.Ordinal) && model.GetSymbolInfo(invocation).Symbol is IMethodSymbol called &&
                    called.ContainingType.ToDisplayString() == "System.IO.File" &&
                    new[] { "Write", "Create", "Delete", "Move", "Copy", "Append" }.Any(prefix => called.Name.StartsWith(prefix, StringComparison.Ordinal)))
                    Error("HTTP_USE_CASE_REQUIRED", invocation, called.ToDisplayString());
            }
            if (source.StartsWith("apps/host/Control/Http/", StringComparison.Ordinal))
                foreach (var creation in syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                    if (model.GetTypeInfo(creation).Type is INamedTypeSymbol type &&
                        type.ContainingNamespace.ToDisplayString() == "NexusPipeline.Platform.Storage.Sqlite" &&
                        (type.Name.EndsWith("Store", StringComparison.Ordinal) || type.Name == "StateDatabase"))
                        Error("HTTP_USE_CASE_REQUIRED", creation, type.ToDisplayString());
            if (source.StartsWith("src/platform/Storage/Execution/", StringComparison.Ordinal))
                foreach (var creation in syntax.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
                    if (model.GetTypeInfo(creation).Type is INamedTypeSymbol configuration &&
                        configuration.ContainingNamespace.ToDisplayString() == "NexusPipeline.Platform.Storage.Configuration" &&
                        configuration.Name is "ConfigurationRunController" or "ConfigurationEditExchange")
                        Error("CONFIGURATION_SESSION_FACTORY_REQUIRED", creation, configuration.ToDisplayString());
            foreach (var identifier in syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var referencedType = model.GetSymbolInfo(identifier).Symbol;
                var dependency = referencedType as INamedTypeSymbol ?? referencedType?.ContainingType;
                if (source == "src/platform/Storage/Configuration/ConfigurationRunController.cs" &&
                    dependency?.ContainingNamespace.ToDisplayString() == "NexusPipeline.Platform.Storage.Execution" &&
                    dependency.Name is "StepWriterScope" or "StepResultCodec" or "AttemptWriterState" or "ExecutionInvocationStore")
                    Error("CONFIGURATION_WRITER_OWNER_REQUIRED", identifier, dependency.ToDisplayString());
                var declaration = identifier.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
                if (declaration is null || model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol owner || !symbols.TryGetValue(owner, out var from)) continue;
                var referenced = model.GetSymbolInfo(identifier).Symbol;
                var target = referenced as INamedTypeSymbol ?? referenced?.ContainingType;
                if (target is not null && symbols.TryGetValue(target, out var to) && from != to) graph[from].Add(to);
            }
            void Error(string rule, SyntaxNode node, string? target) => errors.Add(new { rule, source, target,
                line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1 });
        }
        var colors = new Dictionary<string, int>();
        var stack = new List<string>();
        foreach (var node in graph.Keys.Order(StringComparer.Ordinal)) Visit(node);
        Console.WriteLine(JsonSerializer.Serialize(new { scope = "Roslyn invocation/type rules and Core.Settings symbol dependencies",
            files = trees.Length, settingsOwners = graph.Count, edges = graph.SelectMany(pair => pair.Value.Select(target => new { source = pair.Key, target })), errors }));
        return errors.Count == 0 ? 0 : 1;

        void Visit(string node)
        {
            if (colors.GetValueOrDefault(node) == 2) return;
            if (colors.GetValueOrDefault(node) == 1)
            {
                var location = locations[node];
                errors.Add(new { rule = "SETTINGS_OWNER_CYCLE", source = location.Source, line = location.Line,
                    target = string.Join(" -> ", stack.Skip(stack.IndexOf(node)).Append(node)) });
                return;
            }
            colors[node] = 1; stack.Add(node);
            foreach (var target in graph[node].Order(StringComparer.Ordinal)) Visit(target);
            stack.RemoveAt(stack.Count - 1); colors[node] = 2;
        }
    }
}
