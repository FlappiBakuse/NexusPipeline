using System.Text.Json;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class SourceAdapterTests
{
    private const string Script = """
        globalThis.nexusDiscoverScript = input => {
          const files = JSON.parse(nexusListSource('config'));
          const title = JSON.parse(nexusReadSource('config/' + files[0].name)).name;
          return {...input, direction:'response', candidates:[{candidateId:'account',title,executable:'app.exe',
            arguments:[],resources:[{resourceId:'main',path:'config/account.json',syntax:'json'}],logPaths:[]}],diagnostics:[]};
        };
        """;

    [Fact]
    public async Task UsesReadAuthority()
    {
        await using var fixture = new TaskScriptFixture(); Enable(fixture);
        await fixture.ActivateAsync();
        var calls = new List<string>();
        var result = fixture.Executor(Script).DiscoverSource(Guid.Parse(fixture.Request.OperationId), "sample-adapter",
            path => { calls.Add(path); return "[{\"name\":\"account.json\",\"kind\":\"file\"}]"; },
            path => { calls.Add(path); return "{\"name\":\"Selected account\"}"; }, default);
        Assert.Equal(new[] { "config", "config/account.json" }, calls);
        Assert.Equal("Selected account", Assert.Single(result.Candidates!).Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiresManifestPermission(bool declared)
    {
        await using var fixture = new TaskScriptFixture(); Enable(fixture);
        fixture.Manifest = declared ? fixture.Manifest with { Permissions = new() } : fixture.Manifest with
            { Specialized = fixture.Manifest.Specialized! with { SourceDiscovery = false } };
        await fixture.ActivateAsync();
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Executor(Script).DiscoverSource(Guid.NewGuid(), "sample-adapter",
            _ => throw new InvalidOperationException("must_not_read"), _ => throw new InvalidOperationException("must_not_read"), default));
    }

    [Fact]
    public async Task RejectsUnboundResponse()
    {
        await using var fixture = new TaskScriptFixture(); Enable(fixture); await fixture.ActivateAsync();
        const string wrong = "globalThis.nexusDiscoverScript = input => ({...input, direction:'response',extensionId:'foreign-adapter',candidates:[],diagnostics:[]});";
        Assert.Throws<JsonException>(() => fixture.Executor(wrong).DiscoverSource(Guid.NewGuid(), "sample-adapter", _ => "[]", _ => "{}", default));
        await fixture.Scope.DisposeAsync();
        Assert.ThrowsAny<Exception>(() => fixture.Executor(Script).DiscoverSource(Guid.NewGuid(), "sample-adapter", _ => "[]", _ => "{}", default));
    }

    private static void Enable(TaskScriptFixture fixture) => fixture.Manifest = fixture.Manifest with
    {
        Specialized = fixture.Manifest.Specialized! with { SourceDiscovery = true },
        Permissions = new() { FileAccess = ["selected-script-root"] }
    };
}
