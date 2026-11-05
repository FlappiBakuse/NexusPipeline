using System.Text.Json;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ScriptSourceTests
{
    [Fact]
    public void HoldsReadBytes()
    {
        using var fixture = new ProjectSourceFixture();
        fixture.Write("account.json", "{\"name\":\"账号\"}"u8.ToArray());
        using var source = new ScriptSourceDirectory(fixture.Path, fixture.Catalog.Resources, () => { }, default);
        using var listing = JsonDocument.Parse(source.List(""));
        Assert.Contains(listing.RootElement.EnumerateArray(), item => item.GetProperty("name").GetString() == "account.json");
        Assert.Equal("{\"name\":\"账号\"}", source.Read("account.json"));
        Assert.Throws<IOException>(() => fixture.Write("account.json", "{}"u8.ToArray()));
        source.RequireCurrent(); source.Dispose();
        fixture.Write("account.json", "{}"u8.ToArray());
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("D:/outside.json")]
    [InlineData("account.json:stream")]
    [InlineData("config\\account.json")]
    [InlineData("ACCOUNT.JSON")]
    [InlineData("")]
    public void RejectsForeignPaths(string path)
    {
        using var fixture = new ProjectSourceFixture(); fixture.Write("account.json", "{}"u8.ToArray());
        using var source = new ScriptSourceDirectory(fixture.Path, fixture.Catalog.Resources, () => { }, default);
        Assert.ThrowsAny<Exception>(() => source.Read(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetectsChangedCandidates(bool missing)
    {
        using var fixture = new ProjectSourceFixture();
        using var source = new ScriptSourceDirectory(fixture.Path, fixture.Catalog.Resources, () => { }, default);
        Assert.NotNull(source.List(missing ? "new" : ""));
        if (missing) Directory.CreateDirectory(Path.Combine(fixture.Path, "new"));
        else fixture.Write("new.json", "{}"u8.ToArray());
        Assert.Throws<IOException>(source.RequireCurrent);
    }

    [Fact]
    public void CancelsAndReleases()
    {
        using var fixture = new ProjectSourceFixture(); fixture.Write("account.json", "{}"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        using var source = new ScriptSourceDirectory(fixture.Path, fixture.Catalog.Resources, () => { }, cancellation.Token);
        Assert.Equal("{}", source.Read("account.json")); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => source.List("")); source.Dispose();
        fixture.Write("account.json", "{\"changed\":true}"u8.ToArray());
    }

    [Fact]
    public void BoundsFileInput()
    {
        using var fixture = new ProjectSourceFixture(); fixture.Write("large.json", new byte[8 * 1024 * 1024 + 1]);
        using var source = new ScriptSourceDirectory(fixture.Path, fixture.Catalog.Resources, () => { }, default);
        Assert.Equal("script_source_limit", Assert.Throws<IOException>(() => source.Read("large.json")).Message);
        fixture.Write("large.json", "{}"u8.ToArray());
    }
}
