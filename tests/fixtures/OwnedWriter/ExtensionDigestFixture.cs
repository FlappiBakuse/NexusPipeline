using System.Text.Json;
using NexusPipeline.Platform.Storage.Extensions;

internal static class ExtensionDigestFixture
{
    internal static int Run(string input, string report)
    {
        OwnedFixtureEvidence.RequirePath(input); OwnedFixtureEvidence.RequirePath(report);
        var request = JsonSerializer.Deserialize<ExtensionApplyRequest>(File.ReadAllBytes(input))!;
        File.WriteAllText(report, request.Identity(Guid.Parse("a1234567-89ab-4cde-8123-456789abcdef"), "digest-subject").RequestDigest);
        return 0;
    }
}
