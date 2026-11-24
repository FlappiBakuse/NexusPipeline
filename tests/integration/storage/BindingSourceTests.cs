using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Settings;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BindingSourceTests
{
    [Fact(DisplayName = "First Configuration Selection Is Atomic And Independent For Two Users Of The Same Installed Script")]
    public async Task SelectsIndependentConfigurations()
    {
        await using var fixture = new InstalledBettergiFixture();
        await fixture.InstallAsync();
        var source = fixture.Source;
        var directory = Path.Combine(source.Root, "binding-source");
        Directory.CreateDirectory(Path.Combine(directory, "User", "OneDragon"));
        File.Copy(source.Image, Path.Combine(directory, "BetterGI.exe"));
        foreach (var name in new[] { "first", "second" })
            File.WriteAllText(Path.Combine(directory, "User", "OneDragon", name + ".json"),
                "{\"Name\":\"" + name + "\",\"TaskEnabledList\":{\"领取邮件\":true},\"CompletionAction\":\"无\"}");
        File.WriteAllText(Path.Combine(directory, "User", "config.json"), "{\"selectedOneDragonFlowConfigName\":\"first\"}");
        var originals = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var catalog = source.Definitions.Read();
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Binding selection source", AutomationRootKind.Script, directory);
        var prior = catalog.Scripts.Single(item => item.Id == source.OtherScriptId);
        var access = new ConfigurationDocumentAccess([new(new([]), true)], [new(new([]), true)], []);
        var script = prior with { RootId = root.Id, Executable = new(root.Id, "BetterGI.exe"), Arguments = ["startOneDragon", "first"],
            ConfigurationPaths = [new(root.Id, "User/OneDragon/first.json"), new(root.Id, "User/config.json")],
            LogPaths = [], AdapterResources = [new("one-dragon", 0, "", 1, ConfigurationSyntax.Json, access),
                new("extra-user-config", 1, "", 1, ConfigurationSyntax.Json, access)] };
        var first = new AutomationBinding(Guid.NewGuid(), 0, source.UserA, script.Id, false, 0);
        var second = new AutomationBinding(Guid.NewGuid(), 0, source.UserC, script.Id, false, 0);
        var changes = new AutomationChanges(catalog.Revision, [root], [script], [], [first, second], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "binding-source", changes), changes, DateTimeOffset.UtcNow);
        catalog = source.Definitions.Read(); script = catalog.Scripts.Single(item => item.Id == script.Id);
        var installed = new ExtensionInstalledEvidence(source.Instance, source.Database, new(), fixture.Installation.Roots).ReadCurrent("bettergi")!;
        var imports = new ConfigurationImportService(source.Instance, source.Database, new(), source.Lease, fixture.Execution.Resources);
        var sources = new ScriptSourceService(source.Instance, source.Database, source.Lease, fixture.Execution.Resources, new(),
            fixture.Installation.Roots, fixture.Installation.Activator);
        var service = new BindingSourceImport(source.Database, sources, imports);
        ConfigurationImportRequest Request(Guid binding, string name) => new(Guid.NewGuid(), binding, 1, script.Revision,
            [new("one-dragon", 0), new("extra-user-config", 0)])
            { CandidateId = "User/OneDragon/" + name + ".json", ExpectedInstalledRevision = installed.Metadata.Revision };
        var a = Request(first.Id, "first"); var b = Request(second.Id, "second");
        await Assert.ThrowsAsync<ConfigurationException>(() => service.ImportAsync("binding-source", a with { CandidateId = "missing" }, () => {}, default).AsTask());
        var revisions = new ConfigurationRevisionStore(source.Instance, source.Database, new());
        using var cancellation = new CancellationTokenSource();
        var revoked = false;
        var rejectedAt = new List<BatchPersistencePoint>();
        var guarded = new ConfigurationImportService(source.Instance, source.Database, new(), source.Lease, fixture.Execution.Resources,
            new Observer((point, operation) =>
            {
                if (point != BatchPersistencePoint.OperationAccepted) return;
                rejectedAt.Add(point);
                if (operation == a.OperationId) revoked = true;
                if (operation == b.OperationId) cancellation.Cancel();
            }));
        var control = new ImportUseCases(guarded, new(), new(source.Database, sources, guarded));
        var caller = new AuthenticatedControlRequest(ControlOrigin.Local, "binding-source", null, () => revoked
            ? throw new UnauthorizedAccessException("control_credential_revoked") : new(true, true));
        using (var body = StrictJson.Parse(BoundedJson.Serialize(Wire(a))))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => control.ExecuteAsync(caller, body.RootElement, default).AsTask());
        revoked = false;
        using (var body = StrictJson.Parse(BoundedJson.Serialize(Wire(b))))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => control.ExecuteAsync(caller, body.RootElement, cancellation.Token).AsTask());
        Assert.Equal(new[] { BatchPersistencePoint.OperationAccepted, BatchPersistencePoint.OperationAccepted }, rejectedAt);
        Assert.Null(guarded.ReadAccepted("binding-source", a));
        Assert.Null(guarded.ReadAccepted("binding-source", b));
        Assert.Null(revisions.Read(new(first.Id, BindingConfigurationSource.ResourceId)));
        Assert.Null(revisions.Read(new(second.Id, BindingConfigurationSource.ResourceId)));
        await ImportOverHttpAsync(source.Instance.InstanceId, new(imports, new(), service), a);
        Assert.True((await service.ImportAsync("binding-source", b, () => {}, default)).IsNew);
        await Assert.ThrowsAsync<ConfigurationException>(() => service.ImportAsync("binding-source", Request(first.Id, "second"), () => {}, default).AsTask());
        var saved = source.Definitions.Read();
        var firstBinding = saved.Bindings.Single(item => item.Id == first.Id);
        var secondBinding = saved.Bindings.Single(item => item.Id == second.Id);
        var firstScript = BindingScriptSource.Resolve(script, firstBinding);
        var secondScript = BindingScriptSource.Resolve(script, secondBinding);
        Assert.Equal("first", firstScript.Arguments.Last()); Assert.Equal("second", secondScript.Arguments.Last());
        Assert.EndsWith("first.json", firstScript.ConfigurationPaths[0].RelativePath);
        Assert.EndsWith("second.json", secondScript.ConfigurationPaths[0].RelativePath);
        var views = new ConfigurationOverviewReader(source.Instance, source.Database, source.Lease, new());
        Assert.Contains(views.Read(first.Id).Resources, item => item.Label.EndsWith("first.json"));
        Assert.Contains(views.Read(second.Id).Resources, item => item.Label.EndsWith("second.json"));
        var wire = new ContractReader().Read<NexusPipeline.Platform.Serialization.Protocol.AutomationBundle>(
            new AutomationBundleCodec(new()).Encode(saved, DateTimeOffset.UtcNow), "automation-bundle.schema.json");
        var frozen = BindingSourceCodec.Resolve(wire.Scripts.Single(item => item.Id == script.Id.ToString("D")),
            wire.Bindings.Single(item => item.Id == second.Id.ToString("D")).Source);
        Assert.Equal("second", frozen.Arguments.Last());
        foreach (var item in originals) Assert.Equal(item.Value, File.ReadAllBytes(item.Key));
        fixture.Inputs.VerifyUnchanged();
    }

    private static object Wire(ConfigurationImportRequest request) => new
    {
        format = "nexus.control.configuration-import", version = 1, operationId = request.OperationId,
        bindingId = request.BindingId, expectedBindingRevision = request.ExpectedBindingRevision,
        expectedScriptRevision = request.ExpectedScriptRevision, candidateId = request.CandidateId,
        expectedInstalledRevision = request.ExpectedInstalledRevision,
        resources = request.Resources.Select(item => new { resourceId = item.ResourceId, expectedRevision = item.ExpectedRevision })
    };

    private static async Task ImportOverHttpAsync(Guid instanceId, ImportUseCases imports, ConfigurationImportRequest request)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        var authority = new ControlAuthentication(instanceId, false, null, () => new(0, HostSettings.Default), null!, TimeProvider.System);
        app.Use(ControlExceptionBoundary.InvokeAsync); app.Use(authority.InvokeAsync);
        new ConfigurationImportControl(authority, imports).Map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(address) };
        foreach (var isNew in new[] { true, false })
        {
            using var response = await http.PostAsJsonAsync("/control/v1/configuration/import", Wire(request));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(request.OperationId, result.GetProperty("operationId").GetGuid());
            Assert.Equal(isNew, result.GetProperty("isNew").GetBoolean());
        }
        await app.StopAsync();
    }

    private sealed class Observer(Action<BatchPersistencePoint, Guid> reached) : IBatchPersistenceObserver
    {
        public void Reached(BatchPersistencePoint point, Guid operationId, int resourceOrdinal) => reached(point, operationId);
    }
}
