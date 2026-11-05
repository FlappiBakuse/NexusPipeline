using System.Runtime.Versioning;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AccountBrowserTests
{
    [Fact(DisplayName = "The Actual Product Account Card Routes The Worst Specialized Binding To Its Own History While Activity And Future Schedule Remain Independent")]
    public async Task OpensWorstHistory()
    {
        using var inputs = new BuiltExtensionInputs("emulator-support", "NEXUS_FORMAL_EMULATOR_PACKAGE_RECEIPT");
        var application = Environment.GetEnvironmentVariable("NEXUS_ACCEPTANCE_APPLICATION")
            ?? throw new IOException("NEXUS_ACCEPTANCE_APPLICATION_required");
        if (!Path.IsPathFullyQualified(application) || !File.Exists(Path.Combine(application, "NexusPipeline.exe")))
            throw new IOException("explicit_acceptance_application_required");
        object? context = null;
        await FrontendBrowserTests.RunAsync(inputs, "NEXUS_ACCOUNT_SUMMARY_BROWSER_SCRIPT", 5,
            seed: catalog => context = AccountBrowserFixture.Seed(catalog), browserContext: () => context,
            controlledPackage: AccountBrowserFixture.Package, application: application, timeoutSeconds: 180);
    }
}
