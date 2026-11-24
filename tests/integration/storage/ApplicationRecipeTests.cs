using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Maintenance;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ApplicationRecipeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturesExactPayload(bool delta)
    {
        using var fixture = new ApplicationRecipeFixture(delta);
        using var archive = fixture.Open();
        var recipe = fixture.Read(archive);
        var candidate = recipe.Capture(archive, fixture.Before, fixture.Payload, fixture.Feed, fixture.Blobs, fixture.Contracts, default);
        Assert.Equal(ApplicationTree.Capture(new(SnapshotBudget.Default), fixture.Candidate, default).Entries, candidate.Snapshot.Entries);
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["NexusPipeline.exe"] = [11, 17, 23],
            ["NexusPipeline.update.exe"] = [31, 37, 41],
            ["README.md"] = "candidate"u8.ToArray(),
            ["resources/desktop/NexusPipeline.Desktop.exe"] = [43, 47],
            ["resources/desktop/locales/en-US.pak"] = [61, 67],
            ["resources/desktop/locales/zh-CN.pak"] = [71, 73],
            ["resources/desktop/new.bin"] = [7, 11],
            ["resources/desktop/resources/app.asar"] = [53, 59],
            ["resources/payload-manifest.json"] = fixture.Manifest
        };
        var files = candidate.Snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.File).ToArray();
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), files.Select(file => file.RelativePath).Order(StringComparer.Ordinal));
        foreach (var file in files)
        {
            var bytes = expected[file.RelativePath];
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Assert.Equal(bytes.LongLength, file.SizeBytes);
            Assert.Equal(digest, file.Sha256);
            using var stored = fixture.Blobs.OpenVerified(Assert.Single(candidate.Contents, blob => blob.Sha256 == digest));
            using var actual = new MemoryStream();
            stored.CopyTo(actual);
            Assert.Equal(bytes, actual.ToArray());
        }
        Assert.DoesNotContain(candidate.Snapshot.Entries, entry => entry.RelativePath.StartsWith("package-info/", StringComparison.Ordinal));
        Assert.Equal(delta, !recipe.Reused.IsEmpty);
        if (delta)
        {
            Assert.Equal("resources/desktop/obsolete.bin", Assert.Single(recipe.Removed).RelativePath);
            Assert.Equal(expected.Keys.Where(path => path is not ("README.md" or "resources/desktop/new.bin" or "resources/payload-manifest.json"))
                .Order(StringComparer.Ordinal), recipe.Reused.Select(file => file.RelativePath).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(archive.Files, file => recipe.Reused.Any(reused => reused.RelativePath == file.RelativePath));
            Assert.All(recipe.Reused, file => Assert.Contains(fixture.Before.Contents,
                blob => blob.Sha256 == file.Sha256 && blob.SizeBytes == file.SizeBytes));
        }
    }

    [Theory]
    [InlineData("releaseTag")]
    [InlineData("releaseId")]
    [InlineData("assetId")]
    [InlineData("assetSha256")]
    [InlineData("payloadManifestSha256")]
    [InlineData("removed")]
    [InlineData("reused")]
    public void RejectsUnboundBase(string fault)
    {
        using var fixture = new ApplicationRecipeFixture(delta: true);
        var recipe = fixture.Recipe.DeepClone();
        if (fault is "releaseId" or "assetId") recipe["base"]![fault] = 42;
        else if (fault == "releaseTag") recipe["base"]![fault] = "v0.18.1";
        else if (fault is "assetSha256" or "payloadManifestSha256") recipe["base"]![fault] = new string('9', 64);
        else if (fault == "removed") recipe["removedFiles"]!.AsArray().Clear();
        using var archive = fixture.Open(recipe);
        var parsed = fixture.Read(archive);
        var before = fixture.Before;
        if (fault == "reused")
        {
            var name = parsed.Reused[0].RelativePath;
            File.WriteAllBytes(Path.Combine(fixture.Original, name), [97]);
            before = new FileTreeArchive(new(SnapshotBudget.Default), fixture.Blobs).CaptureApplication(fixture.Original, default);
        }
        Assert.Throws<IOException>(() => parsed.RequireBefore(before, fixture.Payload, fixture.Feed, fixture.Blobs, fixture.Contracts));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("overlap")]
    [InlineData("manual")]
    [InlineData("metadata")]
    public void RejectsUnboundRecipe(string fault)
    {
        using var fixture = new ApplicationRecipeFixture(delta: true);
        var recipe = fixture.Recipe.DeepClone();
        if (fault == "missing") recipe["includedFiles"]!.AsArray().RemoveAt(0);
        if (fault == "overlap") recipe["removedFiles"]!.AsArray().Add(recipe["reusedFiles"]![0]!.DeepClone());
        if (fault == "manual") recipe["manualCover"]!["allowed"] = true;
        using var archive = fixture.Open(recipe, unknownMetadata: fault == "metadata");
        Assert.Throws<IOException>(() => fixture.Read(archive));
    }

    [Fact]
    public void BindsManifestIdentity()
    {
        using var fixture = new ApplicationRecipeFixture();
        var manifest = JsonNode.Parse(fixture.Manifest)!;
        manifest["sourceSha"] = null; manifest["sourceTreeSha"] = null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        _ = ApplicationManifest.Read(fixture.Contracts, bytes, "test");
        Assert.Throws<IOException>(() => ApplicationManifest.Read(fixture.Contracts, bytes, "production"));
        manifest["desktop"]!["runtimeInventorySha256"] = new string('9', 64);
        Assert.Throws<IOException>(() => ApplicationManifest.Read(fixture.Contracts, JsonSerializer.SerializeToUtf8Bytes(manifest), "test"));
    }
}
