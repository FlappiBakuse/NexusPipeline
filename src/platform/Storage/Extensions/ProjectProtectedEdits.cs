using System.Text.Json;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ProjectInputIntent(string Key, long ExpectedRevision, SecretDisposition Disposition);
internal sealed record ProjectProtectedEdit(ProjectInputIntent Intent, SecretValue? Value);

internal sealed class ProjectProtectedEdits : IDisposable
{
    internal IReadOnlyList<ProjectProtectedEdit> Values { get; }
    internal ProjectInputIntent[] Intents => Values.Select(value => value.Intent).ToArray();

    private ProjectProtectedEdits(IReadOnlyList<ProjectProtectedEdit> values) => Values = values;

    internal static ProjectProtectedEdits Read(JsonElement body, bool material)
    {
        var values = new List<ProjectProtectedEdit>();
        try
        {
            if (body.TryGetProperty("protectedInputs", out var inputs))
            {
                if (inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() > 32) throw Invalid();
                foreach (var input in inputs.EnumerateArray())
                {
                    if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Any(pair =>
                        pair.Name is not ("key" or "expectedRevision" or "intent" or "value"))) throw Invalid();
                    var key = input.GetProperty("key").GetString() ?? throw Invalid();
                    if (key is not { Length: > 0 and <= 80 } || key.Any(value => value is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
                        !input.GetProperty("expectedRevision").TryGetInt64(out var revision) || revision is < 0 or >= SecretFieldReference.MaximumRevision) throw Invalid();
                    var intent = input.GetProperty("intent").GetString() switch
                    { "keep" => SecretDisposition.Keep, "set" => SecretDisposition.Set, "clear" => SecretDisposition.Clear, _ => throw Invalid() };
                    var hasValue = input.TryGetProperty("value", out var value);
                    if (hasValue != (material && intent == SecretDisposition.Set)) throw Invalid();
                    SecretValue? secret = null;
                    if (hasValue)
                    {
                        var text = value.GetString() ?? throw Invalid();
                        if (text.Length is < 1 or > 4096) throw Invalid();
                        secret = new SecretValue(text.AsSpan());
                    }
                    values.Add(new(new(key, revision, intent), secret));
                }
                if (values.Select(value => value.Intent.Key).Distinct(StringComparer.Ordinal).Count() != values.Count) throw Invalid();
            }
            return new(values);
        }
        catch { foreach (var value in values) value.Value?.Dispose(); throw; }
    }

    public void Dispose() { foreach (var value in Values) value.Value?.Dispose(); }
    private static IOException Invalid() => new("project_protected_input_invalid");
}
