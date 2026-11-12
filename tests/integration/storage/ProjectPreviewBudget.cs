using System.Text.Json;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

internal static class ProjectPreviewBudget
{
    internal static async Task VerifyAsync(ProjectCompilationService compilation, InstallRuntimeFixture provider, ProjectSourceFixture project)
    {
        var catalog = project.Catalog;
        await using var previews = new ProjectPreviewService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources,
            new(), provider.Roots, provider.Activator);
        var large = JsonSerializer.SerializeToElement(new { previewPages = 72 });
        var entries = new List<(string Subject, Guid Id)>();
        for (var index = 0; index < 7; index++)
        {
            var subject = "preview-" + index;
            var value = await previews.PrepareAsync(compilation, subject, project.Binding, 1, "maas", () => { }, default, selection: large);
            Assert.Equal(73, value.Assets.Count); entries.Add((subject, value.PreviewId));
        }
        Assert.Equal("project_preview_capacity", (await Assert.ThrowsAsync<IOException>(() => previews.PrepareAsync(compilation,
            "overflow", project.Binding, 1, "maas", () => { }, default, selection: large).AsTask())).Message);
        Assert.True(previews.Release(entries[0].Subject, entries[0].Id)); entries.RemoveAt(0);
        var replacement = await previews.PrepareAsync(compilation, "replacement", project.Binding, 1, "maas", () => { }, default, selection: large);
        entries.Add(("replacement", replacement.PreviewId));
        foreach (var (subject, id) in entries) Assert.True(previews.Release(subject, id));
        entries.Clear();
        var small = JsonSerializer.SerializeToElement(new { });
        for (var index = 0; index < 8; index++)
        {
            var subject = "small-" + index;
            var value = await previews.PrepareAsync(compilation, subject, project.Binding, 1, "maas", () => { }, default, selection: small);
            entries.Add((subject, value.PreviewId));
        }
        Assert.Equal("project_preview_capacity", (await Assert.ThrowsAsync<IOException>(() => previews.PrepareAsync(compilation,
            "ninth", project.Binding, 1, "maas", () => { }, default, selection: small).AsTask())).Message);
        foreach (var (subject, id) in entries) Assert.True(previews.Release(subject, id));
    }
}
