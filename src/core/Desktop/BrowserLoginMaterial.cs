using System.Collections.Immutable;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Core.Desktop;

public sealed class BrowserLoginMaterial : IDisposable
{
    public IReadOnlyDictionary<string, SecretValue> Values { get; }
    public BrowserLoginMaterial(IReadOnlyDictionary<string, SecretValue> values)
    {
        if (values.Count is < 1 or > 28) throw new ArgumentException("browser_login_values_invalid");
        var owned = new Dictionary<string, SecretValue>(StringComparer.Ordinal);
        try
        {
            foreach (var (id, value) in values)
            {
                SecretValue? copy = null; value.Use(text => copy = new(text)); owned.Add(id, copy!);
            }
            Values = owned.ToImmutableDictionary(StringComparer.Ordinal);
        }
        catch { foreach (var value in owned.Values) value.Dispose(); throw; }
    }
    public void Dispose() { foreach (var value in Values.Values) value.Dispose(); }
    public override string ToString() => "[browser login material]";
}
