using System.Text;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ProjectConfigTests
{
    [Theory(DisplayName = "The Actual Sdk Source Port Presents Saved Configuration Metadata And Virtual Members Without Leaking Live Account Members")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresentsSavedMounts(bool file)
    {
        using var fixture = new ProjectSourceFixture(configuration: true, file: file); using var source = fixture.Open(); using var mounts = Open(fixture, source);
        using var port = new SelectedSourcePort(source, () => { }, default, mounts);
        var metadata = Assert.Single(port.Configurations);
        Assert.Equal("account", metadata.ResourceId); Assert.Equal(1, metadata.Revision);
        Assert.Equal(mounts.Mounts[0].Manifest.Sha256, metadata.SnapshotSha256);
        File.Delete(fixture.ConfigurationFile!);
        if (!file) File.WriteAllText(Path.Combine(fixture.Path, "config", "other.json"), "{\"account\":\"other\"}");
        var root = await port.ListAsync("", default); Assert.True(root.Exists);
        var entry = root.Entries.Single(item => item.Name == (file ? "local.json" : "config"));
        var path = file ? "local.json" : "config/local.json";
        if (!file)
        {
            var directory = await port.ListAsync("config", default); entry = Assert.Single(directory.Entries);
            Assert.Equal("local.json", entry.Name); Assert.Null(await port.ReadAsync("config/other.json", default));
        }
        var saved = await port.ReadAsync(path, default); Assert.NotNull(saved); Assert.Equal(entry.Identity, saved.Identity);
        Assert.Contains("saved-account", Encoding.UTF8.GetString(saved.Content.Span), StringComparison.Ordinal);
        _ = source.Freeze(default); _ = mounts.Capture();
        port.Dispose(); Assert.Throws<ObjectDisposedException>(() => port.Configurations);
    }

    [Theory(DisplayName = "A Project Configuration Mount Reads Its Exact Saved Binding Snapshot Instead Of The Current Live Account Bytes")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsSavedConfig(bool file)
    {
        using var fixture = new ProjectSourceFixture(configuration: true, file: file); using var source = fixture.Open();
        using var mounts = Open(fixture, source); var path = file ? "local.json" : "config/local.json";
        File.WriteAllText(fixture.ConfigurationFile!, "{\"account\":\"other-live-account\"}");
        Assert.True(mounts.TryRead(path, out var captured, default)); Assert.NotNull(captured);
        Assert.Contains("saved-account", Encoding.UTF8.GetString(captured.Content.Span), StringComparison.Ordinal);
        Assert.DoesNotContain("other-live-account", Encoding.UTF8.GetString(captured.Content.Span), StringComparison.Ordinal);
        System.Runtime.InteropServices.MemoryMarshal.AsMemory(captured.Content).Span.Fill(0);
        Assert.True(mounts.TryRead(path, out var repeated, default));
        Assert.Contains("saved-account", Encoding.UTF8.GetString(repeated!.Content.Span), StringComparison.Ordinal);
        var original = Assert.Single(mounts.Capture()); Assert.Equal(fixture.Address, original.Source.Address);
        Assert.Equal(captured.Sha256, original.Content.Sha256); Assert.Equal(1, original.Source.ExpectedRevision);
        Assert.Single(mounts.ReadSet); _ = source.Freeze(default); mounts.RequireCurrent();
        Assert.Contains("other-live-account", File.ReadAllText(fixture.ConfigurationFile!), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Missing Members Of A Saved Directory Never Fall Back To A New Live File And The Whole Saved Revision Remains A Read Dependency")]
    public void FreezesSavedAbsence()
    {
        using var fixture = new ProjectSourceFixture(configuration: true); using var source = fixture.Open(); using var mounts = Open(fixture, source);
        File.WriteAllText(Path.Combine(fixture.Path, "config", "missing.json"), "{\"account\":\"other\"}");
        Assert.True(mounts.TryRead("config/missing.json", out var absent, default)); Assert.Null(absent);
        Assert.False(mounts.TryRead("interface.json", out _, default));
        Assert.Single(mounts.ReadSet); Assert.Equal(1, mounts.ReadSet[0].ExpectedRevision); mounts.RequireCurrent();
        Assert.Null(fixture.Catalog.Resources.TryAcquire(Guid.NewGuid(),
            [new(ConfigurationRevisionService.ResourceKey(fixture.Address), ResourceClaimMode.Mutation)]).Lease);
        var saved = fixture.Revisions.Read(fixture.Address)!;
        fixture.Save(new(fixture.Address, 1, 1, saved.Contract, saved.Tree, ConfigurationUpdateKind.Manual));
        Assert.Throws<IOException>(mounts.RequireCurrent);
    }

    [Fact(DisplayName = "Saved Configuration And Raw Project Files Share One Actual Compiler File Budget")]
    public void SharesSourceBudget()
    {
        using var fixture = new ProjectSourceFixture(configuration: true); using var source = fixture.Open(); using var mounts = Open(fixture, source);
        for (var index = 0; index < 128; index++) fixture.Write($"source-{index}.json", "{}"u8.ToArray());
        for (var index = 0; index < 127; index++) Assert.NotNull(source.Read($"source-{index}.json", default));
        Assert.True(mounts.TryRead("config/local.json", out var config, default)); Assert.NotNull(config);
        Assert.Throws<IOException>(() => source.Read("source-127.json", default));
        _ = source.Freeze(default); Assert.Single(mounts.Capture());
    }

    [Fact(DisplayName = "Configuration Mounts Reject Another Instance Owner And Case Aliases Of Saved Paths")]
    public void RequiresMountOwner()
    {
        using var fixture = new ProjectSourceFixture(configuration: true); using var other = new ProjectSourceFixture(); using var source = fixture.Open();
        Assert.Throws<IOException>(() => new ProjectConfigMounts(other.Catalog.Instance, other.Catalog.Database,
            other.Catalog.Lease, other.Catalog.Resources, source));
        using var mounts = Open(fixture, source);
        Assert.Throws<IOException>(() => mounts.TryRead("CONFIG/local.json", out _, default));
        Assert.Throws<IOException>(() => mounts.TryRead("config/LOCAL.JSON", out _, default));
        mounts.Dispose(); Assert.Throws<ObjectDisposedException>(() => mounts.TryRead("config/local.json", out _, default));
    }

    private static ProjectConfigMounts Open(ProjectSourceFixture fixture, SelectedProjectSource source) =>
        new(fixture.Catalog.Instance, fixture.Catalog.Database, fixture.Catalog.Lease, fixture.Catalog.Resources, source);
}
