using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JudgementFilesTests
{
    [Fact(DisplayName = "An Opened Directory Capability Rejects A Real Junction Introduced Before Its Next Relative Open")]
    public void RejectsIntroducedJunctions()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "judgement-file-kernel-" + Guid.NewGuid().ToString("N"));
        var selected = Path.Combine(root, "selected"); var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(Path.Combine(selected, "local")); Directory.CreateDirectory(outside);
        using var scope = PhysicalDirectoryHandle.Open(selected);
        using var local = scope.AcquireDirectory("local");
        ControlledJunction.Create(Path.Combine(selected, "linked"), outside);
        Assert.Throws<IOException>(() => scope.AcquireDirectory("linked"));
    }

    [Fact(DisplayName = "Actual Javascript File Functions Read Declared Scopes Write Script Files And Keep Configuration Bytes Read Only")]
    public async Task ScopesJavascriptFiles()
    {
        await using var fixture = new JudgementWorkerFixture("javascript", """
function nexusJudge(input) {
  const files = nexus.files.list();
  const checks = {
    scriptRead: nexus.files.readText('source.txt') === 'source 雪',
    configurationRead: nexus.files.readText(input.configurations[1].targetPath + '/settings.txt') === 'external configuration',
    outsideRead: nexus.files.readText(input.outside) === null && nexus.files.readText('../outside.txt') === null,
    nestedWrite: nexus.files.writeText('nested/result.txt', 'saved 雪'),
    siblingWrite: nexus.files.writeText('shared/result.txt', 'allowed sibling'),
    configurationFile: !nexus.files.writeText('shared/settings.txt', 'changed'),
    configurationDirectory: !nexus.files.writeText('configuration/settings.txt', 'changed') && !nexus.files.writeText('configuration/new.txt', 'changed'),
    outsideWrite: !nexus.files.writeText('../outside.txt', 'changed') && !nexus.files.writeText(input.outside, 'changed'),
    metadata: Array.isArray(files) && files.length === 1 && files[0].root === 'script' &&
      files[0].path === 'source.txt' && files[0].absolutePath === input.files[0].absolutePath && typeof files[0].GetType === 'undefined',
    native: typeof nexus.files.GetType === 'undefined' && typeof nexus.files.readText.GetType === 'undefined' && typeof nexus.readFile === 'undefined'
  };
  return {outcome:Object.values(checks).every(value => value) ? 'succeeded' : 'failed',reason:JSON.stringify(checks)};
}
""");
        var directory = fixture.DirectoryPath; File.WriteAllText(Path.Combine(directory, "source.txt"), "source 雪", new UTF8Encoding(false));
        var configuration = Path.Combine(directory, "configuration"); Directory.CreateDirectory(configuration);
        File.WriteAllText(Path.Combine(configuration, "settings.txt"), "keep configuration");
        var externalConfiguration = directory + "-configuration"; Directory.CreateDirectory(externalConfiguration);
        File.WriteAllText(Path.Combine(externalConfiguration, "settings.txt"), "external configuration");
        var shared = Path.Combine(directory, "shared"); Directory.CreateDirectory(shared);
        var sharedConfiguration = Path.Combine(shared, "settings.txt"); File.WriteAllText(sharedConfiguration, "keep shared configuration");
        var outside = directory + "-outside.txt"; File.WriteAllText(outside, "keep outside");
        var input = Input(fixture, [configuration, externalConfiguration, sharedConfiguration], outside,
            [new { root = "script", path = "source.txt", absolutePath = Path.Combine(directory, "source.txt") }]);
        await fixture.StartAsync(); var exchange = await fixture.InvokeAsync(1, true, input);
        await fixture.AssertExitedAsync();
        var result = Assert.IsType<BusinessJudgementResult>(exchange.Response.Result);
        Assert.True(result.Outcome == BusinessOutcome.Succeeded, result.Reason);
        Assert.Equal("saved 雪", File.ReadAllText(Path.Combine(directory, "nested/result.txt")));
        Assert.Equal("allowed sibling", File.ReadAllText(Path.Combine(shared, "result.txt")));
        Assert.Equal("keep shared configuration", File.ReadAllText(sharedConfiguration));
        Assert.Equal("keep configuration", File.ReadAllText(Path.Combine(configuration, "settings.txt")));
        Assert.Equal(new[] { Path.Combine(configuration, "settings.txt") }, Directory.GetFileSystemEntries(configuration));
        Assert.Equal("external configuration", File.ReadAllText(Path.Combine(externalConfiguration, "settings.txt")));
        Assert.Equal("keep outside", File.ReadAllText(outside));
    }

    [Theory(DisplayName = "Actual Javascript File Functions Reject Physical Aliases Without Reading Or Changing Their Targets")]
    [InlineData("hardlink")]
    [InlineData("junction")]
    [InlineData("dangling-junction")]
    public async Task RejectsPhysicalAliases(string kind)
    {
        await using var fixture = new JudgementWorkerFixture("javascript", """
function nexusJudge(input) {
  const denied = nexus.files.readText(input.relative) === null && !nexus.files.writeText(input.relative, 'must not write');
  return {outcome:denied ? 'succeeded' : 'failed',reason:'physical alias rejected'};
}
""");
        var outsideDirectory = fixture.DirectoryPath + "-outside"; Directory.CreateDirectory(outsideDirectory);
        var outside = Path.Combine(outsideDirectory, "target.txt"); File.WriteAllText(outside, "keep original bytes");
        var relative = kind == "hardlink" ? "link.txt" : "linked/target.txt";
        if (kind == "hardlink") Assert.True(CreateHardLink(Path.Combine(fixture.DirectoryPath, relative), outside, IntPtr.Zero));
        else
        {
            ControlledJunction.Create(Path.Combine(fixture.DirectoryPath, "linked"), outsideDirectory);
            if (kind == "dangling-junction") { File.Delete(outside); Directory.Delete(outsideDirectory); }
        }
        await fixture.StartAsync(); var exchange = await fixture.InvokeAsync(1, true, Input(fixture, [], outside, [], relative));
        await fixture.AssertExitedAsync();
        Assert.Equal(BusinessOutcome.Succeeded, Assert.IsType<BusinessJudgementResult>(exchange.Response.Result).Outcome);
        if (kind == "dangling-junction") Assert.False(Directory.Exists(outsideDirectory));
        else Assert.Equal("keep original bytes", File.ReadAllText(outside));
    }

    [Fact(DisplayName = "Actual Javascript File Functions Bound Reads And Reject Oversized Writes Before Changing Existing Bytes")]
    public async Task BoundsFileWrites()
    {
        await using var fixture = new JudgementWorkerFixture("javascript", """
function nexusJudge(input) {
  const boundary = nexus.files.readText('boundary.txt');
  const oversized = '雪'.repeat(Math.floor(2097152 / 3) + 1);
  const denied = boundary.length === 2097152 && nexus.files.readText('oversized.txt') === null &&
    !nexus.files.writeText('boundary.txt', oversized) && !nexus.files.writeText('never-created/result.txt', oversized) &&
    nexus.files.readText('invalid.txt') === null && nexus.files.readText(1) === null && !nexus.files.writeText({}, 'bad') && nexus.files.list(1) === null;
  return {outcome:denied ? 'succeeded' : 'failed',reason:'file byte bounds preserved'};
}
""", timeoutSeconds: 10);
        var directory = fixture.DirectoryPath; var boundary = Path.Combine(directory, "boundary.txt");
        File.WriteAllBytes(boundary, Enumerable.Repeat((byte)'x', JudgementFileAccess.MaximumFileBytes).ToArray());
        File.WriteAllBytes(Path.Combine(directory, "oversized.txt"), new byte[JudgementFileAccess.MaximumFileBytes + 1]);
        File.WriteAllBytes(Path.Combine(directory, "invalid.txt"), [0xff, 0xff]);
        await fixture.StartAsync(); var exchange = await fixture.InvokeAsync(1, true, Input(fixture, [], "", []));
        await fixture.AssertExitedAsync();
        Assert.Equal(BusinessOutcome.Succeeded, Assert.IsType<BusinessJudgementResult>(exchange.Response.Result).Outcome);
        Assert.Equal(new string('x', JudgementFileAccess.MaximumFileBytes), File.ReadAllText(boundary));
        Assert.False(Path.Exists(Path.Combine(directory, "never-created")));
    }

    private static string Input(JudgementWorkerFixture fixture, string[] configurations, string outside, object[] files, string relative = "") =>
        JsonSerializer.Serialize(new { format = "nexus.execution.judgement-input", scriptDir = fixture.DirectoryPath,
            configurations = configurations.Select(target => new { targetPath = target }).ToArray(), files, outside, relative });

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string filename, string existingFilename, IntPtr reserved);
}
