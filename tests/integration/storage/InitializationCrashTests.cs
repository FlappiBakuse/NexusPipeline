using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InitializationCrashTests
{
    [Fact(DisplayName = "Killing The Actual Host During Original Maa Initialization Preserves The Unclosed Scene And Refuses Replay On Startup")]
    public async Task PreservesUnclosedInitialization()
    {
        using var driver = new BuiltExtensionInputs("maa-framework", "NEXUS_PACKAGED_MAA_RECEIPT");
        using var consumer = new BuiltExtensionInputs("maas", "NEXUS_PACKAGED_MAAS_RECEIPT");
        await using var host = new FrontendHostFixture();
        ProjectSourceFixture? source = null; string marker = "";
        await host.StartAsync(driver, additional: [consumer], seed: catalog =>
        {
            source = new(configuration: true, file: true, enabled: true, catalog: catalog, configurationName: "mxu.json");
            MaaInitializationSource.CopyProgram(source.Path); MaaBrowserTests.Seed(source);
            marker = Path.Combine(source.Path, "crashed-initialization");
            MaaInitializationSource.Prepare(source, driver.Archive, marker, false, true);
            File.AppendAllText(source.ConfigurationFile!, "\n ");
        });
        Assert.NotNull(source);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = host.Address, Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);
        var original = File.ReadAllBytes(source.ConfigurationFile!);
        _ = await PostAsync(http, "/control/v1/projects/select", new { format = "nexus.control.project-command", version = 1, action = "select",
            operationId = Guid.NewGuid(), bindingId = source.Binding, expectedBindingRevision = 1, expectedSelectionRevision = 0, extensionId = "maas",
            selection = new { InterfacePath = "assets/interface.json", ClientKind = "mxu", Language = "zh_cn",
                Runtime = new { NativeDirectory = "native" }, Source = new { Kind = "mxu", Path = "mxu.json", InstanceId = "one" } } });
        var definitions = new AutomationDefinitionStore(source.Catalog.Database).Read();
        var settings = await http.GetFromJsonAsync<JsonElement>("/control/v1/settings");
        var operation = Guid.NewGuid();
        var accepted = await PostAsync(http, "/control/v1/execution/start/" + operation.ToString("D"), new {
            format = "nexus.control.execution-selection", version = 1, target = new { kind = "projectInitialization", bindingId = source.Binding },
            expectedCatalogRevision = definitions.Revision, expectedSettingsRevision = settings.GetProperty("saved").GetProperty("revision").GetInt64() });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!File.Exists(marker)) await Task.Delay(50, deadline.Token);
        var instance = source.Catalog.Instance; var database = source.Catalog.Database;
        Guid journal;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true)) journal = StartSourceRows.Read(connection, transaction, operation).JournalId;
        var state = AttemptWriterState.Read(instance, database, new(), journal);
        var binding = StepResultCodec.Binding(state.Latest.Root, state.Steps.Single());
        var root = new StepEvidenceReader(instance, database, new()).Context(binding).Root;
        Assert.NotNull(root); Assert.Null(state.Total.Steps[0].Target);
        await host.KillOwnerAsync();
        while (WriterStopEvidence.ObserveProcess(root) == WriterState.Running) await Task.Delay(20, deadline.Token);
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(root));
        using var lease = InstanceLease.Acquire(instance, new());
        var installed = File.ReadAllBytes(source.ConfigurationFile!);
        Assert.NotEqual(original, installed);
        var frozen = AttemptWriterState.Read(instance, database, new(), journal);
        for (var repetition = 0; repetition < 2; repetition++)
        {
            var error = Assert.Throws<IOException>(() => StateDatabase.OpenExisting(instance, Path.Combine(host.Root, "crash-diagnostics"),
                new JournalStartupAdmission(instance, new())));
            Assert.Equal("startup_journal_writer_stop_unconfirmed", error.Message);
            Assert.Equal(frozen.Latest.Root.GetRawText(), AttemptWriterState.Read(instance, database, new(), journal).Latest.Root.GetRawText());
            Assert.Equal(installed, File.ReadAllBytes(source.ConfigurationFile!));
            Assert.Equal(new[] { "initialized" }, File.ReadAllLines(marker));
            Assert.Null(new AttemptReceiptStore(database).Read(accepted.GetProperty("attemptId").GetGuid()));
        }
        driver.VerifyUnchanged(); consumer.VerifyUnchanged(); source.Dispose();
    }

    private static async Task<JsonElement> PostAsync(HttpClient http, string route, object body)
    {
        using var response = await http.PostAsJsonAsync(route, JsonSerializer.SerializeToElement(body));
        Assert.True(response.IsSuccessStatusCode, route + ":" + await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
