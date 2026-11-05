using System.Collections.Immutable;
using NexusPipeline.Core.Desktop;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class BrowserPolicyTests
{
    [Fact(DisplayName = "Frozen Login Policy Retains Explicit Origin And Finite Selector Completion Checks Without Aliasing Caller Collections")]
    public void FreezesFinitePolicy()
    {
        var input = Policy(); var frozen = BrowserPolicyRules.Freeze(input, Grants);
        Assert.Equal(input.Id, frozen.Id); Assert.Equal(input.StartUrl, frozen.StartUrl);
        Assert.Equal(input.Cookies.ToArray(), frozen.Cookies.ToArray()); Assert.NotEqual(input.Cookies, frozen.Cookies);
        Assert.Equal(input.JsonRequests[0].Selector.ToArray(), frozen.JsonRequests[0].Selector.ToArray());
        Assert.NotEqual(input.JsonRequests[0].Selector, frozen.JsonRequests[0].Selector);
        Assert.Equal(new[] { "token", "account" }, frozen.ReadySets[0].ToArray());
        Assert.NotEqual(input.ReadySets[0], frozen.ReadySets[0]); Assert.Equal("id", Assert.Single(frozen.Checks.Single(check => check.Id == "device").Path));
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
