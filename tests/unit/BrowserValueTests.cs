using NexusPipeline.Core.Desktop;
using NexusPipeline.Core.Secrets;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class BrowserValueTests
{
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
            foreach (var value in values.Values) Assert.Equal("[secret value]", value.ToString());
        }
        finally { foreach (var value in values.Values) value.Dispose(); }
    }
}
