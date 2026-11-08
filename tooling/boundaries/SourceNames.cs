using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class SourceNames
{
    internal static int Verify(string root, string inputs)
    {
        root = Path.GetFullPath(root);
        using var input = JsonDocument.Parse(File.ReadAllText(inputs));
        var exceptions = new Dictionary<string, (int Count, string Reason)>();
        foreach (var file in input.RootElement.GetProperty("exceptionFiles").EnumerateArray())
        {
            using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, file.GetString()!)));
            foreach (var entry in registry.RootElement.EnumerateArray())
            {
                var key = Key(entry.GetProperty("path").GetString()!, entry.GetProperty("kind").GetString()!,
                    entry.GetProperty("name").GetString()!);
                exceptions.Add(key, (entry.GetProperty("count").GetInt32(), entry.GetProperty("reason").GetString()!));
            }
        }
        var actual = new Dictionary<string, int>();
        var errors = new List<string>();
        var declarations = 0;
        foreach (var file in input.RootElement.GetProperty("files").EnumerateArray())
        {
            var relative = file.GetString()!;
            if (!relative.EndsWith(".cs", StringComparison.Ordinal)) continue;
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, relative)),
                new CSharpParseOptions(LanguageVersion.CSharp14));
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var name = node switch
                {
                    BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                    DelegateDeclarationSyntax type => type.Identifier.ValueText,
                    MethodDeclarationSyntax method => method.Identifier.ValueText,
                    LocalFunctionStatementSyntax method => method.Identifier.ValueText,
                    _ => null
                };
                if (name is null) continue;
                declarations++;
                if (WordCount(name) <= 3) continue;
                var kind = node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax ? "type" : "method";
                var key = Key(relative, kind, name);
                actual[key] = actual.GetValueOrDefault(key) + 1;
                if (!exceptions.TryGetValue(key, out var exemption) || string.IsNullOrWhiteSpace(exemption.Reason))
                    errors.Add(relative + ": " + name + " exceeds three words without a reviewed exception");
            }
        }
        foreach (var (key, exception) in exceptions)
            if (actual.GetValueOrDefault(key) != exception.Count)
                errors.Add(key + ": reviewed declaration count changed");
        Console.WriteLine(JsonSerializer.Serialize(new { declarations, reviewedExceptions = exceptions.Count, errors }));
        return errors.Count == 0 ? 0 : 1;
    }

    private static string Key(string path, string kind, string name) => path + "|" + kind + "|" + name;

    private static int WordCount(string name)
    {
        name = Regex.Replace(name, "^I(?=[A-Z][a-z])", "");
        name = name.Replace("MuMu", "Mumu", StringComparison.Ordinal);
        return Regex.Matches(name, "[A-Z]+(?=[A-Z][a-z]|[0-9]|$)|[A-Z]?[a-z]+[0-9]*|[0-9]+").Count;
    }
}
