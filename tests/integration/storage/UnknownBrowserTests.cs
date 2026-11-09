using System.Runtime.Versioning;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class UnknownBrowserTests
{
    [Fact(DisplayName = "The Original BAAH Package Keeps Unknown Enabled And Disabled Rows Through Actual Host Execution Safe Retry And Localized Frozen History")]
    public async Task PreservesUnknownTasks()
    {
        using var inputs = new BuiltExtensionInputs("baah", "NEXUS_FORMAL_BAAH_PACKAGE_RECEIPT");
        var application = Environment.GetEnvironmentVariable("NEXUS_ACCEPTANCE_APPLICATION")
            ?? throw new IOException("NEXUS_ACCEPTANCE_APPLICATION_required");
        if (!Path.IsPathFullyQualified(application) || !File.Exists(Path.Combine(application, "NexusPipeline.exe")))
            throw new IOException("explicit_acceptance_application_required");
        object? context = null;
        await FrontendBrowserTests.RunAsync(inputs, "NEXUS_UNKNOWN_TASK_BROWSER_SCRIPT",
            seed: catalog => context = UnknownBrowserFixture.Seed(catalog), browserContext: () => context,
            application: application, timeoutSeconds: 240);
    }
}
