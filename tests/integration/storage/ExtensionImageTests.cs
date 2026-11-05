using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExtensionImageTests
{
    private static byte[] Image(byte suffix = 0) => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jC1sAAAAASUVORK5CYII=")
        .Concat(new[] { suffix }).ToArray();

    [Fact(DisplayName = "Installed Images Share One Document Cas Deduplicate And Retire Only After The Last Saved Reference")]
    public async Task CommitsReferences()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await FrontendInstalledTests.OpenAsync(fixture);
        var first = Image(); var second = Image(1);
        var firstId = ExtensionDocumentRows.Digest(first); var secondId = ExtensionDocumentRows.Digest(second);
        var operation = Guid.NewGuid();
        try
        {
            var input = Input("settings", 0, [firstId], first);
            _ = await Invoke(frontend, "image-write", input, operation);
            _ = await Invoke(frontend, "image-write", Input("other", 0, [firstId], first), Guid.NewGuid());
            Assert.Single((await Invoke(frontend, "image-list", new { name = "settings" })).EnumerateArray());
            _ = await Invoke(frontend, "image-write", Input("settings", 1, [secondId], second), Guid.NewGuid());
            Assert.Equal(Convert.ToBase64String(first), (await Invoke(frontend, "image-read", new { id = firstId })).GetProperty("Content").GetString());
            var replay = await Invoke(frontend, "image-write", input, operation);
            Assert.False(replay.GetProperty("IsNew").GetBoolean()); Assert.Equal(1, replay.GetProperty("Revision").GetInt64());
            _ = await Invoke(frontend, "image-write", Input("other", 1, [], null), Guid.NewGuid());
            await Assert.ThrowsAsync<IOException>(() => Invoke(frontend, "image-read", new { id = firstId }));
            Assert.Equal(secondId, (await Invoke(frontend, "image-read", new { id = secondId })).GetProperty("Info").GetProperty("Id").GetString());
            var failed = Guid.NewGuid();
            await Assert.ThrowsAsync<SqliteException>(() => Invoke(frontend, "image-write", Input("settings", 2, [new string('a', 64)], null), failed));
            var saved = await Invoke(frontend, "image-write", Input("settings", 2, [secondId], null), failed);
            Assert.Equal(3, saved.GetProperty("Revision").GetInt64());
            await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(frontend, "image-write", Input("settings", 2, [], null), Guid.NewGuid()));
            Assert.Single((await Invoke(frontend, "image-list", new { name = "settings" })).EnumerateArray());
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
        await Assert.ThrowsAnyAsync<Exception>(() => Invoke(frontend, "image-read", new { id = secondId }));
    }

    [Fact(DisplayName = "Capacity Failure Rolls Back The Image Its References And The Document Without Consuming The Operation")]
    public async Task RollsBackCapacity()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await FrontendInstalledTests.OpenAsync(fixture); var ids = new List<string>();
        try
        {
            for (byte index = 0; index < 32; index++)
            {
                var image = Image(index); ids.Add(ExtensionDocumentRows.Digest(image));
                _ = await Invoke(frontend, "image-write", Input("settings", index, ids.ToArray(), image), Guid.NewGuid());
            }
            var addition = Image(32); var id = ExtensionDocumentRows.Digest(addition); var operation = Guid.NewGuid();
            await Assert.ThrowsAsync<IOException>(() => Invoke(frontend, "image-write", Input("overflow", 0, [id], addition), operation));
            Assert.Empty((await Invoke(frontend, "image-list", new { name = "overflow" })).EnumerateArray());
            await Assert.ThrowsAsync<IOException>(() => Invoke(frontend, "image-read", new { id }));
            _ = await Invoke(frontend, "image-write", Input("overflow", 0, [], null), operation);
            Assert.Equal(32, (await Invoke(frontend, "image-list", new { name = "settings" })).GetArrayLength());
            Assert.Equal(32, Count(fixture, "extension_images"));
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory(DisplayName = "Unrecognized Retirement Bytes Or Prior Orphans Preserve The Original Image And Saved Document")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesUnknownImages(bool orphan)
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await FrontendInstalledTests.OpenAsync(fixture); var image = Image(); var id = ExtensionDocumentRows.Digest(image);
        try
        {
            _ = await Invoke(frontend, "image-write", Input("settings", 0, [id], image), Guid.NewGuid());
            var catalog = fixture.Package.Catalog;
            using var connection = DatabaseConnections.Open(catalog.Database.DatabasePath);
            using var command = connection.CreateCommand();
            var unknown = orphan ? image : Image(7);
            command.CommandText = orphan ? "DELETE FROM extension_image_references WHERE extension_id='fixture-plugin';" :
                "UPDATE extension_images SET content=$bytes WHERE extension_id='fixture-plugin';";
            command.Parameters.AddWithValue("$bytes", unknown); command.ExecuteNonQuery();
            await Assert.ThrowsAsync<IOException>(() => Invoke(frontend, "image-write", Input("settings", 1, [], null), Guid.NewGuid()));
            Assert.Equal(orphan ? 0 : 1, (await Invoke(frontend, "image-list", new { name = "settings" })).GetArrayLength());
            command.CommandText = "SELECT content FROM extension_images WHERE extension_id='fixture-plugin';";
            Assert.Equal(unknown, Assert.IsType<byte[]>(command.ExecuteScalar()));
            command.CommandText = "SELECT revision FROM extension_documents WHERE extension_id='fixture-plugin' AND name='settings';";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    private static object Input(string name, long revision, string[] ids, byte[]? image) => new
    { name, revision, ids, value = new { selected = ids.FirstOrDefault() }, addition = image is null ? null : new { type = "image/png", content = Convert.ToBase64String(image) } };

    private static async Task<JsonElement> Invoke(InstalledFrontend frontend, string operation, object input, Guid? operationId = null)
    {
        var result = await frontend.InvokeAsync(operation, operationId, "images:caller", false,
            JsonSerializer.SerializeToUtf8Bytes(input), () => { }, default);
        using var parsed = JsonDocument.Parse(result.Content); return parsed.RootElement.Clone();
    }

    private static long Count(InstallRuntimeFixture fixture, string table)
    {
        using var connection = DatabaseConnections.Open(fixture.Package.Catalog.Database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table;
        return (long)command.ExecuteScalar()!;
    }
}
