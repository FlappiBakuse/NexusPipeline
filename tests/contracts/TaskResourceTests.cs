using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Extensions;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class TaskResourceTests
{
    [Theory(DisplayName = "The Actual Interpreter Receives Host Parsed Values From The Original Declared Json Jsonc And Yaml Bytes")]
    [InlineData(ConfigurationSyntax.Json, "{\"title\":\"中文\",\"list\":[true,2]}")]
    [InlineData(ConfigurationSyntax.JsonWithComments, "{/* keep */\"title\":\"中文\",\"list\":[true,2,],}")]
    [InlineData(ConfigurationSyntax.Yaml, "title: 中文\nlist: [true, 2]\nnotify: |-\n  first\n  second\n")]
    public async Task ReadsDeclaredSemantics(ConfigurationSyntax syntax, string text)
    {
        await using var fixture = new TaskScriptFixture(); await fixture.ActivateAsync();
        var bytes = Encoding.UTF8.GetBytes(text); var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var resource = TaskResourceValue.Create(fixture.Identity.OperationId, "main", 7, digest, bytes, syntax);
        fixture.Request = fixture.Request with { Resources = [resource] };
        var result = fixture.Execute(fixture.Executor("""
            globalThis.nexusTaskAdapter = input => ({...input,direction:'response',resources:undefined,
              tasks:[{taskId:'parsed',title:input.resources[0].value.title+':'+input.resources[0].value.list[1],
                isSelected:true,isEnabled:true,dependencies:[],retrySafety:'unknown'}]});
            """));
        Assert.Equal("中文:2", Assert.Single(result.Tasks!).Title);
        Assert.Equal(text, resource.Content); Assert.Equal(digest, resource.Sha256); Assert.Equal(7, resource.Revision);
        Assert.Equal(Encoding.UTF8.GetBytes(text), bytes);
    }

    [Theory(DisplayName = "A Caller Cannot Substitute Parsed Semantics Or Lie About The Original Declared Syntax")]
    [InlineData("value")]
    [InlineData("missing")]
    [InlineData("opaque")]
    [InlineData("syntax")]
    public async Task RejectsChangedSemantics(string fault)
    {
        await using var fixture = new TaskScriptFixture(); await fixture.ActivateAsync();
        var bytes = "{\"selected\":true}"u8.ToArray();
        var resource = TaskResourceValue.Create(fixture.Identity.OperationId, "main", 1,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes, ConfigurationSyntax.Json);
        resource = fault switch
        {
            "value" => resource with { Value = JsonSerializer.SerializeToElement(new { selected = false }) },
            "missing" => resource with { Value = null }, "opaque" => resource with { Syntax = "opaque" },
            _ => resource with { Syntax = null }
        };
        fixture.Request = fixture.Request with { Resources = [resource] };
        Assert.Throws<JsonException>(() => fixture.Execute(fixture.Executor("throw new Error('must_not_execute');")));
    }
}
