using System.Text.Json;
using NexusPipeline.Core.Desktop;
using NexusPipeline.Core.Secrets;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class BrowserValueContractTests
{
    public static IEnumerable<object[]> Vectors()
    {
        using var stream = typeof(BrowserValueContractTests).Assembly.GetManifestResourceStream("browser-login-values.json")!;
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.EnumerateArray()) yield return [row.GetProperty("name").GetString()!, row.GetRawText()];
    }

    [Theory(DisplayName = "Desktop And Core Apply The Same Shared Login Value Contract")]
    [MemberData(nameof(Vectors))]
    public void AppliesSharedValues(string name, string descriptor)
    {
        _ = name;
        using var document = JsonDocument.Parse(descriptor);
        var row = document.RootElement; var form = row.GetProperty("form").GetString();
        var json = form is "jsonDepth" or "arrayDepth" or "jsonText" or "literalJson";
        var text = row.TryGetProperty("text", out var literal) ? literal.GetString()! : "";
        if (form is "jsonDepth" or "arrayDepth")
        {
            var depth = row.GetProperty("depth").GetInt32() - 1;
            text = "{\"id\":\"value\",\"extra\":" + string.Concat(Enumerable.Repeat(form == "jsonDepth" ? "{\"x\":" : "[", depth)) +
                "0" + new string(form == "jsonDepth" ? '}' : ']', depth) + "}";
            if (row.TryGetProperty("pad", out var pad)) text = text.PadRight(pad.GetInt32());
        }
        else if (form is "raw" or "jsonText")
        {
            text = string.Concat(Enumerable.Repeat(row.GetProperty("character").GetString(), row.GetProperty("repeat").GetInt32()));
            if (form == "jsonText") text = "{\"id\":\"" + text + "\"}";
        }
        var policy = BrowserPolicyRules.Freeze(new("fixture", "https://login.example.test/", ["https://login.example.test"], [], [],
            [new("one", "https://login.example.test", "localStorage", "one"), new("two", "https://login.example.test", "localStorage", "two"),
                new("alternative", "https://login.example.test", "localStorage", "alternative")], [],
            form is "equal" or "missing" or "alternative" ? [["one", "two"], ["alternative"]] : [["one"]],
            [new("one", form == "token" ? "token" : "nonEmpty", json ? ["id"] : [], 32768)], [["one", "two"]]), ["https://login.example.test"]);
        var values = new Dictionary<string, SecretValue> { [form == "alternative" ? "alternative" : "one"] = new(form == "alternative" ? "ready" : text) };
        if (form == "equal") values["two"] = new(row.GetProperty("other").GetString()!);
        try { Assert.Equal(row.GetProperty("expected").GetBoolean(), BrowserValueRules.IsReady(policy, values)); }
        finally { foreach (var value in values.Values) value.Dispose(); }
    }
}
