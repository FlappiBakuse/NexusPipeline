using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExtensionDigestTests
{
    [Fact]
    public async Task BindsAcrossProcesses()
    {
        using var fixture = new PayloadSourceFixture();
        var locales = new Dictionary<string, ExtensionLocaleText>
        {
            ["zh-CN"] = new("本地化插件", "游戏", "摘要"), ["en-US"] = new("Localized plugin", "Game", "Summary")
        }.ToImmutableDictionary(StringComparer.Ordinal);
        var metadata = new ExtensionMetadata("zh-CN", "本地化插件", "游戏", "摘要", "Owner", [], locales, null, null);
        var request = ExtensionApplyRequest.FromSource(fixture.Source);
        request = request with { Candidate = request.Candidate! with { Metadata = metadata } };
        var operation = Guid.Parse("a1234567-89ab-4cde-8123-456789abcdef");
        var expected = request.Identity(operation, "digest-subject").RequestDigest;
        var input = Path.Combine(fixture.Package.Root, "digest-input.json");
        File.WriteAllBytes(input, BoundedJson.Serialize(request));
        for (var index = 0; index < 4; index++)
        {
            var report = Path.Combine(fixture.Package.Root, $"digest-{index}.txt");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var argument in new[] { "process-output", "extension-request-digest", input, report }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(expected, File.ReadAllText(report));
        }
        var changed = request with { Candidate = request.Candidate with { Metadata = metadata with
            { Locales = locales.SetItem("en-US", locales["en-US"] with { Summary = "Changed" }) } } };
        Assert.NotEqual(expected, changed.Identity(operation, "digest-subject").RequestDigest);
    }
}
