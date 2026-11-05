using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Host.Control.Cli;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ImportControlTests
{
    [Fact]
    public async Task ImportsThroughHost()
    {
        Guid binding = Guid.Empty; string? resource = null; string? source = null;
        await using var fixture = await ControlHttpFixture.StartAsync((instance, database, _, _, _, _) =>
        {
            var changes = AutomationDefinitionSeed.Create(Path.Combine(Path.GetDirectoryName(instance.RuntimeRoot)!, "sources"));
            var path = new AutomationPath(changes.Roots[0].Id, "config.json");
            source = Path.Combine(changes.Roots[0].Path, "config.json"); File.WriteAllText(source, "{\"original\":true}");
            binding = changes.Bindings[0].Id; resource = GenericConfigurationPolicy.ResourceId(path);
            changes = changes with { Scripts = [changes.Scripts[0] with { ConfigurationPaths = [path] }] };
            new AutomationDefinitionStore(database).Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        });
        var command = new { format = "nexus.control.configuration-import", version = 1, operationId = Guid.NewGuid(),
            bindingId = binding, expectedBindingRevision = 1, expectedScriptRevision = 1,
            resources = new[] { new { resourceId = resource, expectedRevision = 0 } } };
        var cli = ControlCliRequest.Parse(["configuration-import", "--address", fixture.Address.ToString().TrimEnd('/'), "--input", "-"]);
        Assert.Equal(HttpMethod.Post, cli.Method); Assert.Equal("/control/v1/configuration/import", cli.Path);
        fixture.Client.DefaultRequestHeaders.Add("Origin", "https://unrelated.invalid");
        using (var denied = await fixture.Client.PostAsJsonAsync(cli.Path, command)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        fixture.Client.DefaultRequestHeaders.Remove("Origin");
        using (var saved = await fixture.Client.PostAsJsonAsync(cli.Path, command))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("isNew").GetBoolean());
            Assert.Equal(1, body.GetProperty("resources")[0].GetProperty("revision").GetInt64());
        }
        File.WriteAllText(source!, "{\"original\":false}");
        using (var replay = await fixture.Client.PostAsJsonAsync(cli.Path, command))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.False((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isNew").GetBoolean());
        }
        using var overview = await fixture.Client.GetFromJsonAsync<JsonDocument>($"/control/v1/configuration/bindings/{binding:D}/resources");
        Assert.True(overview!.RootElement.GetProperty("resources")[0].GetProperty("isSaved").GetBoolean());
        using var original = await fixture.Client.GetFromJsonAsync<JsonDocument>($"/control/v1/configuration/generic/{binding:D}/{resource}/files?revision=1");
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("{\"original\":true}"u8)),
            original!.RootElement.GetProperty("items")[0].GetProperty("sha256").GetString());
        Assert.Equal("{\"original\":false}", File.ReadAllText(source!));
    }
}
