using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SpecializedLocaleTests
{
    [Fact(DisplayName = "Eight Original Specialized Packages Preserve Localized And User Titles In Actual Frozen History After Removal")]
    public async Task PreservesFrozenTitles()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllBytes(Required("NEXUS_SPECIALIZED_LOCALE_FIXTURES")));
        Assert.Equal("nexus.specialized-locale-fixtures", fixtures.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, fixtures.RootElement.GetProperty("version").GetInt32());
        using var packageSet = JsonDocument.Parse(File.ReadAllBytes(Required("NEXUS_PACKAGED_SET")));
        var inputs = new List<BuiltExtensionInputs>();
        try
        {
            foreach (var fixture in fixtures.RootElement.GetProperty("cases").EnumerateArray())
            {
                var id = fixture.GetProperty("extensionId").GetString()!;
                var package = Assert.Single(packageSet.RootElement.GetProperty("runs").EnumerateArray(),
                    item => item.GetProperty("label").GetString() == id + "-package");
                var receipt = package.GetProperty("receipt").GetString()!;
                Assert.Equal(package.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(receipt))));
                var input = new BuiltExtensionInputs(id, receipt, explicitPath: true); inputs.Add(input);
                using var archive = ZipFile.OpenRead(input.Archive);
                using var adapter = Assert.Single(archive.Entries, entry => entry.FullName == "adapter/main.js").Open();
                Assert.Equal(fixture.GetProperty("adapterSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(adapter)));
            }
            Assert.Equal(8, inputs.Count); Assert.Equal(8, inputs.Select(input => input.Manifest["extensionId"]!.GetValue<string>()).Distinct().Count());
            object? context = null;
            await FrontendBrowserTests.RunAsync(inputs[0], "NEXUS_SPECIALIZED_LOCALE_BROWSER_SCRIPT", 25,
                additional: inputs.Skip(1).ToArray(), seed: catalog => context = SpecializedLocaleFixture.Seed(catalog, fixtures.RootElement),
                browserContext: () => context, application: Required("NEXUS_ACCEPTANCE_APPLICATION"), timeoutSeconds: 300);
        }
        finally { foreach (var input in inputs) input.Dispose(); }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new IOException(name + "_required");
}
