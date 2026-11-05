using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class SourceBrowserTests
{
    [Fact]
    public async Task CreatesExplicitInstance()
    {
        using var inputs = new BuiltExtensionInputs("baah", "NEXUS_FORMAL_BAAH_PACKAGE_RECEIPT");
        var directory = "";
        await FrontendBrowserTests.RunAsync(inputs, "NEXUS_SOURCE_BROWSER_SCRIPT", 4, remoteAccess: true,
            seed: catalog =>
            {
                directory = Path.Combine(Path.GetDirectoryName(catalog.Instance.RuntimeRoot)!, "script-source");
                Directory.CreateDirectory(Path.Combine(directory, "BAAH_CONFIGS"));
                Directory.CreateDirectory(Path.Combine(directory, "DATA", "CONFIGS"));
                File.WriteAllText(Path.Combine(directory, "BAAH.exe"), "Controlled file; never executed");
                File.WriteAllText(Path.Combine(directory, "DATA", "CONFIGS", "software_config.json"), "{\"SAVE_LOG_TO_FILE\":true}");
                foreach (var name in new[] { "one.json", "two.json" })
                    File.WriteAllText(Path.Combine(directory, "BAAH_CONFIGS", name), "{\"TASK_ORDER_GROUP\":{\"ACTIVATE_IND\":0,\"ALL_PIPELINES\":[{\"TASK_PIPELINE\":[\"邮件\"],\"TASK_ONOFF\":[true]}]}}");
            }, browserContext: () => new { directory });
    }
}
