using System.Security.Cryptography;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

internal static class ProviderProjectChecks
{
    internal static void Verify(ImmutableBlobStore blobs, CompiledProjectSnapshot snapshot)
    {
        using var input = new ProviderProjectInput(blobs, snapshot);
        ExecutionProjectRequest Request(string kind) => new() { Fingerprint = snapshot.Document.Sha256, Sequence = 1, Kind = kind };
        var manifest = input.Read(Request("manifest"));
        Assert.Equal("root", manifest.RootIndex); Assert.Equal(3, manifest.AssetCount); Assert.Single(manifest.Trees!);
        Assert.Equal("resources", manifest.Trees![0].Name); Assert.Equal("assets", manifest.Trees[0].Path);
        Assert.Null(manifest.Selection);
        var selected = new[] { "occurrence-a", "occurrence-b" };
        using var selectionInput = new ProviderProjectInput(blobs, snapshot, selected);
        selected[0] = "changed";
        var firstSelection = selectionInput.Read(Request("manifest")).Selection!;
        Assert.Equal(snapshot.Value.Consumer.Installed.ExtensionId, firstSelection.ConsumerExtensionId);
        Assert.Equal(snapshot.Value.BindingId.ToString("D"), firstSelection.BindingId);
        Assert.Equal(new[] { "occurrence-a", "occurrence-b" }, firstSelection.SelectedTaskIds);
        Assert.IsType<string[]>(firstSelection.SelectedTaskIds)[0] = "changed-again";
        Assert.Equal("occurrence-a", selectionInput.Read(Request("manifest")).Selection!.SelectedTaskIds[0]);
        var executionIds = new[] { "native-a", "native-b" };
        using var mapped = new ProviderProjectInput(blobs, snapshot, ["business-a", "business-b"], executionIds);
        executionIds[0] = "changed";
        var mapping = mapped.Read(Request("manifest")).Selection!;
        Assert.Equal(new[] { "business-a", "business-b" }, mapping.SelectedTaskIds);
        Assert.Equal(new[] { "native-a", "native-b" }, mapping.ExecutionTaskIds);
        Assert.IsType<string[]>(mapping.ExecutionTaskIds)[0] = "changed-again";
        Assert.Equal("native-a", mapped.Read(Request("manifest")).Selection!.ExecutionTaskIds![0]);
        Assert.Throws<IOException>(() => new ProviderProjectInput(blobs, snapshot, ["a"], ["x", "y"]));
        Assert.Throws<IOException>(() => new ProviderProjectInput(blobs, snapshot, ["a", "b"], ["x", "x"]));
        Assert.Throws<IOException>(() => new ProviderProjectInput(blobs, snapshot, executionTaskIds: ["x"]));
        Assert.Equal(3, input.Read(Request("assets") with { Index = 0 }).Assets!.Count);
        Assert.Empty(input.Read(Request("assets") with { Index = 3 }).Assets!);
        var tree = input.Read(Request("tree") with { Name = "resources", Index = 0 }).Entries!;
        Assert.Equal(3, tree.Count); Assert.Contains(tree, item => item.Path == "image.png" && item.SizeBytes == 4);
        var file = input.Read(Request("file") with { Name = "resources", Path = "image.png", Offset = 1, MaximumBytes = 2 });
        Assert.Equal(new byte[] { 2, 3 }, Convert.FromBase64String(file.ContentBase64!));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3, 4 })), file.Sha256);
        var artifact = snapshot.Value.Assets.Single(item => item.Name == "artifact");
        using var full = new MemoryStream();
        for (long offset = 0; offset < artifact.Content.SizeBytes; offset += 131072)
        {
            var chunk = input.Read(Request("asset") with { Name = "artifact", Offset = offset, MaximumBytes = 131072 });
            Assert.Equal(artifact.Content.Sha256, chunk.Sha256); full.Write(Convert.FromBase64String(chunk.ContentBase64!));
        }
        Assert.Equal(artifact.Content.Sha256, Convert.ToHexStringLower(SHA256.HashData(full.ToArray())));
        Assert.Empty(Convert.FromBase64String(input.Read(Request("asset") with
            { Name = "artifact", Offset = artifact.Content.SizeBytes, MaximumBytes = 1 }).ContentBase64!));
        Assert.Throws<IOException>(() => input.Read(Request("manifest") with { Fingerprint = new string('0', 64) }));
        Assert.Throws<IOException>(() => input.Read(Request("manifest") with { Path = "config/local.json" }));
        Assert.Throws<IOException>(() => input.Read(Request("tree") with { Name = "resources", Index = 4 }));
        Assert.Throws<IOException>(() => input.Read(Request("asset") with { Name = "artifact", Offset = -1, MaximumBytes = 1 }));
        Assert.Throws<IOException>(() => input.Read(Request("asset") with { Name = "artifact", Offset = 0, MaximumBytes = 131073 }));
        foreach (var path in new[] { "../config/local.json", "assets/image.png", "IMAGE.PNG", "C:/private", "image.png/.." })
            Assert.Throws<IOException>(() => input.Read(Request("file") with { Name = "resources", Path = path, Offset = 0, MaximumBytes = 1 }));
        input.Dispose(); Assert.Throws<ObjectDisposedException>(() => input.Read(Request("manifest")));
    }
}
