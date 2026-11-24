using System.Collections.Immutable;
using NexusPipeline.Core.Desktop;
using Xunit;
using NexusPipeline.Core.Secrets;
using System.Text.Json;

namespace NexusPipeline.CoreTests;

public sealed class BrowserPolicyTests
{
    [Fact(DisplayName = "Frozen Login Policy Retains Explicit Origin And Finite Selector Completion Checks")]
    public void FreezesFinitePolicy()
    {
        var input = Policy(); var frozen = BrowserPolicyRules.Freeze(input, Grants);
        Assert.Equal(input.Id, frozen.Id); Assert.Equal(input.StartUrl, frozen.StartUrl);
        Assert.Equal(input.Cookies.ToArray(), frozen.Cookies.ToArray());
        Assert.Equal(input.JsonRequests[0].Selector.ToArray(), frozen.JsonRequests[0].Selector.ToArray());
        Assert.Equal(new[] { "token", "account" }, frozen.ReadySets[0].ToArray());
        Assert.Equal("id", Assert.Single(frozen.Checks.Single(check => check.Id == "device").Path));
        var loopback = input with { StartUrl = "http://127.0.0.1:1234/", Origins = ["http://127.0.0.1:1234"], PopupOrigins = [],
            Cookies = [new("token", "127.0.0.1", "/", "token")], Storage = [], JsonRequests = [], ReadySets = [["token"]], Checks = [], EqualSets = [] };
        Assert.Equal(loopback.StartUrl, BrowserPolicyRules.Freeze(loopback, ["http://127.0.0.1:1234"]).StartUrl);
    }

    [Theory(DisplayName = "Malformed Or Ungranted Login Origins Selectors And Completion Constraints Cannot Become A Frozen Native Capture Policy")]
    [InlineData("file")]
    [InlineData("http")]
    [InlineData("origin")]
    [InlineData("popup")]
    [InlineData("cookie")]
    [InlineData("alias")]
    [InlineData("storage")]
    [InlineData("method")]
    [InlineData("endpoint")]
    [InlineData("prototype")]
    [InlineData("id")]
    [InlineData("ready")]
    [InlineData("empty")]
    [InlineData("check")]
    [InlineData("check-prototype")]
    [InlineData("cookie-path")]
    [InlineData("equal")]
    [InlineData("equal-duplicate")]
    [InlineData("maximum")]
    public void RejectsExpandedPolicy(string fault)
    {
        var input = Policy();
        var changed = fault switch
        {
            "file" => input with { StartUrl = "file:///private" },
            "http" => input with { Origins = ["http://www.example.test"] },
            "origin" => input with { Origins = ["https://ungranted.test"] },
            "popup" => input with { PopupOrigins = ["https://ungranted.test"] },
            "cookie" => input with { Cookies = [input.Cookies[0] with { Domain = ".foreign.test" }] },
            "alias" => input with { Cookies = [.. input.Cookies, input.Cookies[0] with { Id = "alias" }] },
            "storage" => input with { Storage = [input.Storage[0] with { Origin = "https://foreign.test" }] },
            "method" => input with { JsonRequests = [input.JsonRequests[0] with { Method = "PUT" }] },
            "endpoint" => input with { JsonRequests = [input.JsonRequests[0] with { Endpoint = "https://foreign.test/read" }] },
            "prototype" => input with { JsonRequests = [input.JsonRequests[0] with { Selector = ["__proto__"] }] },
            "id" => input with { Cookies = [.. input.Cookies, input.Cookies[0] with { Name = "other" }] },
            "ready" => input with { ReadySets = [["unknown"]] },
            "empty" => input with { ReadySets = [[]] },
            "check" => input with { Checks = [new("unknown", "header", [], 32)] },
            "check-prototype" => input with { Checks = [new("device", "header", ["constructor"], 32)] },
            "cookie-path" => input with { Checks = [new("token", "header", ["id"], 32)] },
            "equal" => input with { EqualSets = [["unknown", "account"]] },
            "equal-duplicate" => input with { EqualSets = [["account", "account"]] },
            "maximum" => input with { Checks = [new("token", "header", [], 32769)] },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        var error = Record.Exception(() => BrowserPolicyRules.Freeze(changed, Grants));
        Assert.True(error is ArgumentException or UnauthorizedAccessException);
        Assert.Equal("https://www.example.test/login", input.StartUrl); Assert.Equal(".example.test", input.Cookies[0].Domain);
    }

    private static readonly string[] Grants = ["https://www.example.test", "https://user.example.test", "https://api.example.test"];
    private static BrowserLoginPolicy Policy() => new("fixture", "https://www.example.test/login",
        ["https://www.example.test", "https://user.example.test"], ["https://user.example.test"],
        [new("token", ".example.test", "/", "token"), new("account", ".example.test", "/", "account")],
        [new("device", "https://www.example.test", "localStorage", "device")],
        [new("passport", "https://www.example.test", "https://api.example.test/read", "GET", ["data", "content"])],
        [["token", "account"], ["passport", "device"]],
        [new("token", "header", [], 16384), new("account", "digits", [], 32), new("passport", "token", [], 16384), new("device", "header", ["id"], 128)], []);
}

public sealed class BrowserValueTests
{
    [Fact(DisplayName = "A Captured Login Secret Redacts Its String Representation")]
    public void RedactsCapturedSecret()
    {
        using var value = new SecretValue("synthetic secret");
        Assert.Equal("[secret value]", value.ToString());
    }

    [Theory(DisplayName = "Only Complete Bounded Same Account Captures Can Finish Native Login")]
    [InlineData("complete", true)]
    [InlineData("alternative", true)]
    [InlineData("missing", false)]
    [InlineData("foreign", false)]
    [InlineData("mismatch", false)]
    [InlineData("header", false)]
    [InlineData("token", false)]
    [InlineData("digits", false)]
    [InlineData("large", false)]
    [InlineData("json-number", false)]
    [InlineData("json-missing", false)]
    [InlineData("json-duplicate", false)]
    [InlineData("json-nested", false)]
    [InlineData("json-malformed", false)]
    [InlineData("token-padding", true)]
    [InlineData("token-middle", false)]
    [InlineData("token-empty", false)]
    [InlineData("token-padding-large", false)]
    [InlineData("token-symbol", false)]
    public void RequiresCompleteValues(string mode, bool expected)
    {
        var policy = BrowserPolicyRules.Freeze(new("fixture", "https://login.example.test/", ["https://login.example.test"], [],
            [new("token", ".example.test", "/", "token"), new("account", ".example.test", "/", "account"),
                new("other", ".example.test", "/", "other")],
            [new("device", "https://login.example.test", "localStorage", "device")],
            [new("passport", "https://login.example.test", "https://login.example.test/read", "POST", ["data", "content"])],
            [["token", "account"], ["passport", "device"]],
            [new("token", "token", [], 32), new("account", "digits", [], 32), new("other", "digits", [], 32),
                new("passport", "nonEmpty", [], 128), new("device", "header", ["id"], 32)], [["account", "other"]]), ["https://login.example.test"]);
        var values = new Dictionary<string, SecretValue> { ["token"] = new("synthetic"), ["account"] = new("123") };
        try
        {
            if (mode is "alternative" || mode.StartsWith("json-"))
            {
                foreach (var value in values.Values) value.Dispose(); values.Clear();
                values["passport"] = new("synthetic-passport");
                values["device"] = new(mode switch
                {
                    "json-number" => "{\"id\":1}",
                    "json-missing" => "{\"other\":\"a\"}",
                    "json-duplicate" => "{\"id\":\"a\",\"id\":\"b\"}",
                    "json-nested" => "{\"id\":{\"id\":\"a\"}}",
                    "json-malformed" => "{",
                    _ => "{\"id\":\"synthetic-device\"}"
                });
            }
            else if (mode == "missing") { values["account"].Dispose(); values.Remove("account"); }
            else if (mode is "mismatch" or "foreign") values[mode == "foreign" ? "unknown" : "other"] = new("456");
            else if (mode is "header" or "token" or "digits" or "large" || mode.StartsWith("token-"))
            {
                var id = mode == "digits" ? "account" : "token"; values[id].Dispose();
                values[id] = new(mode switch { "header" => "line\r\nbreak", "token" => "semi;colon", "digits" => "123a",
                    "token-padding" => "synthetic+/==", "token-middle" => "a=b", "token-empty" => "==", "token-padding-large" => "a===",
                    "token-symbol" => "a:b", _ => new string('a', 33) });
            }
            Assert.Equal(expected, BrowserValueRules.IsReady(policy, values));
        }
        finally { foreach (var value in values.Values) value.Dispose(); }
    }
}

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
