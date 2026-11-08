using Json.Schema;
using Json.Schema.Keywords;
using System.Collections.Frozen;
using System.Text.Json;

namespace NexusPipeline.Platform.Serialization;

internal static class ContractSchemas
{
    private static readonly FrozenDictionary<string, string> Resources = typeof(ContractReader).Assembly
        .GetManifestResourceNames().Where(name => name.StartsWith("Contracts.", StringComparison.Ordinal))
        .ToFrozenDictionary(name => name["Contracts.".Length..], StringComparer.Ordinal);
    private static readonly Lazy<Evaluator> Shared = new(() => new());

    internal static void RequireName(string schemaName)
    {
        if (!Resources.ContainsKey(schemaName))
            throw new ArgumentException("Unknown schema identity", nameof(schemaName));
    }

    internal static bool Evaluate(JsonElement value, string schemaName) => Shared.Value.Evaluate(value, schemaName);

    private sealed class Evaluator
    {
        private readonly FrozenDictionary<string, JsonSchema> _schemas;
        private readonly EvaluationOptions _evaluation = new() { RequireFormatValidation = true };
        private readonly ReaderWriterLockSlim _gate = new();
        private readonly HashSet<string> _resolved = new(StringComparer.Ordinal);

        internal Evaluator()
        {
            var formats = new FormatRegistry();
            formats.Register(Formats.Uuid);
            formats.Register(Formats.DateTime);
            formats.Register(new AbsoluteUriFormat());
            _evaluation.FormatRegistry = formats;
            var options = new BuildOptions
            {
                Dialect = Dialect.Draft202012,
                SchemaRegistry = new SchemaRegistry { Fetch = (_, _) => null }
            };
            var schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
            foreach (var (name, resource) in Resources.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                using var stream = typeof(ContractReader).Assembly.GetManifestResourceStream(resource)!;
                using var document = JsonDocument.Parse(stream);
                schemas.Add(name, JsonSchema.Build(document.RootElement.Clone(), options));
            }
            if (schemas.Count == 0) throw new InvalidOperationException("No embedded contract schemas");
            _schemas = schemas.ToFrozenDictionary(StringComparer.Ordinal);
        }

        internal bool Evaluate(JsonElement value, string schemaName)
        {
            var schema = _schemas[schemaName];
            // Resolving one root can rewrite nodes shared with other roots.
            _gate.EnterReadLock();
            try
            {
                if (_resolved.Contains(schemaName)) return schema.Evaluate(value, _evaluation).IsValid;
            }
            finally { _gate.ExitReadLock(); }
            _gate.EnterWriteLock();
            try
            {
                var valid = schema.Evaluate(value, _evaluation).IsValid;
                if (!_resolved.Contains(schemaName) && IsStatic(schema.Root)) _resolved.Add(schemaName);
                return valid;
            }
            finally { _gate.ExitWriteLock(); }
        }

        private static bool IsStatic(JsonSchemaNode root)
        {
            var visited = new HashSet<JsonSchemaNode>();
            var pending = new Stack<JsonSchemaNode>(); pending.Push(root);
            while (pending.TryPop(out var node))
            {
                if (!visited.Add(node)) continue;
                foreach (var keyword in node.Keywords)
                {
                    var name = keyword.Handler is AnnotationKeyword && keyword.Value is string annotation
                        ? annotation : keyword.Handler.Name;
                    if (name is "$dynamicRef" or "$recursiveRef" or "$dynamicAnchor" or "$recursiveAnchor") return false;
                    if (keyword.Handler is RefKeyword && keyword.Subschemas.Length != 1) return false;
                    foreach (var child in keyword.Subschemas) pending.Push(child);
                }
            }
            return true;
        }
    }
}
