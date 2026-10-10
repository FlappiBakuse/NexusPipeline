using System.Text.Json;
using NexusPipeline.Modules.Dashboard;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.Tests.Dashboard;

public sealed class DashboardTests : IDisposable
{
    private sealed class Source : IDashboardCardSource
    {
        public DashboardCard[] Cards = [];
        public int Generation;
        public Action? Reading;
        public event Action? Changed;
        public DashboardSourceSnapshot Read(string locale) { Reading?.Invoke(); return new(Generation.ToString(), Cards); }
        public void Set(params DashboardCard[] cards) { Cards = cards; Generation++; Changed?.Invoke(); }
    }
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dashboard-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "layout.json");
    private static DashboardCard Plugin(bool visible = true) => new("plugin:game-activities:carousel", "plugin", "game-activities", "carousel", "Activities", "", visible, 400);
    private static DashboardSnapshot Save(DashboardService service, DashboardSnapshot baseline, params string[] ids) =>
        service.Save("zh-CN", DashboardRepresentation.Create(baseline).ETag, new(1, baseline.CatalogRevision, ids));
    private static void Failure(DashboardFailureKind expected, Action action) => Assert.Equal(expected, Assert.Throws<DashboardFailure>(action).Kind);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact] public void RegistrationRejectsDuplicatesAndForeignIdsAndDisposesOnce()
    {
        var registry = new PluginDashboardCardRegistry();
        var descriptor = new PluginDashboardCardDescriptor("carousel", "title", "description", true, 400);
        var handle = registry.Register("game-activities", descriptor);
        Assert.Throws<InvalidOperationException>(() => registry.Register("game-activities", descriptor));
        Assert.Throws<ArgumentException>(() => registry.Register("game-activities", descriptor with { Id = "plugin:other:carousel" }));
        long generation = registry.Revision;
        handle.Dispose(); handle.Dispose(); Assert.Empty(registry.Snapshot()); Assert.Equal(generation + 1, registry.Revision);
        using var replacement = registry.Register("game-activities", descriptor); handle.Dispose(); Assert.Single(registry.Snapshot());
    }

    [Fact] public void ConcurrentReadsPersistDefaultsExactlyOnce()
    {
        var source = new Source { Cards = [Plugin()] };
        using var service = new DashboardService(source, FilePath);
        Parallel.For(0, 20, _ => service.Read("zh-CN"));
        var state = service.Read("en-US");
        Assert.Equal(1, state.Layout.Revision);
        Assert.Equal(DashboardLayoutStore.CoreIds.Append(Plugin().CardId), state.Layout.Entries.Select(e => e.CardId));
        using var restarted = new DashboardService(source, FilePath);
        var next = restarted.Read("zh-CN");
        Assert.Equal(state.Layout.LayoutId, next.Layout.LayoutId);
        Assert.Equal(state.Layout.Entries, next.Layout.Entries);
        Assert.NotEqual(state.CatalogRevision, next.CatalogRevision);
    }

    [Fact] public void UnavailableCardKeepsHiddenPreferenceAcrossRestart()
    {
        var source = new Source { Cards = [Plugin()] };
        using (var service = new DashboardService(source, FilePath))
        {
            var initial = service.Read("zh-CN");
            Save(service, initial, DashboardLayoutStore.CoreIds);
        }
        source.Set();
        using var restarted = new DashboardService(source, FilePath);
        var unavailable = restarted.Read("zh-CN"); Assert.Equal(3, unavailable.Cards.Count);
        source.Set(Plugin() with { Title = "Updated", DefaultVisible = true });
        var restored = restarted.Read("zh-CN");
        Assert.False(restored.Layout.Entries.Single(e => e.CardId == Plugin().CardId).Visible);
        Assert.Equal(unavailable.Layout.Revision, restored.Layout.Revision);
    }

    [Fact] public void SaveFailureDoesNotPublishNewLayout()
    {
        var source = new Source();
        using (var first = new DashboardService(source, FilePath)) first.Read("zh-CN");
        byte[] bytes = File.ReadAllBytes(FilePath);
        List<DashboardChange> changes = [];
        using var service = new DashboardService(source, FilePath, (file, content) => JsonUtil.WriteAtomic(file, content,
            phase => { if (phase == JsonWritePhase.BeforeReplace) throw new IOException("controlled"); }), changes.Add);
        var baseline = service.Read("zh-CN"); changes.Clear();
        Failure(DashboardFailureKind.PersistenceFailed, () => Save(service, baseline));
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        Assert.Equal(baseline.Layout.Entries, service.Read("zh-CN").Layout.Entries);
        Assert.DoesNotContain(changes, e => e.Kind == "layout");
        source.Set(Plugin());
        Failure(DashboardFailureKind.PersistenceFailed, () => service.Read("zh-CN"));
        source.Set(); Assert.Equal(baseline.Layout.Revision, service.Read("zh-CN").Layout.Revision);
    }

    [Fact] public void ConditionalWritesHaveOneWinnerAndNoOpDoesNotWrite()
    {
        using var service = new DashboardService(new Source(), FilePath);
        var baseline = service.Read("zh-CN"); byte[] bytes = File.ReadAllBytes(FilePath);
        var noop = Save(service, baseline, baseline.VisibleCardIds);
        Assert.Equal(baseline.Layout.Revision, noop.Layout.Revision); Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        int won = 0, lost = 0;
        Parallel.For(0, 2, _ => { try { Save(service, baseline, "core:running"); Interlocked.Increment(ref won); }
            catch (DashboardFailure failure) when (failure.Kind == DashboardFailureKind.Conflict) { Interlocked.Increment(ref lost); } });
        Assert.Equal(1, won); Assert.Equal(1, lost);
        Assert.Equal(baseline.Layout.Revision + 1, service.Read("zh-CN").Layout.Revision);
    }

    [Fact] public void LanguageAndCatalogChangesRejectStaleRepresentations()
    {
        var source = new Source(); using var service = new DashboardService(source, FilePath);
        var zh = service.Read("zh-CN"); var en = service.Read("en-US");
        Assert.Equal(zh.CatalogRevision, en.CatalogRevision);
        Assert.NotEqual(DashboardRepresentation.Create(zh).ETag, DashboardRepresentation.Create(en).ETag);
        Failure(DashboardFailureKind.Conflict, () => service.Save("en-US", DashboardRepresentation.Create(zh).ETag, new(1, en.CatalogRevision, [])));
        source.Set(Plugin());
        Failure(DashboardFailureKind.CatalogChanged, () => Save(service, zh));
        var current = service.Read("zh-CN"); int reads = 0;
        source.Reading = () => { if (++reads == 2) source.Generation++; };
        Failure(DashboardFailureKind.CatalogChanged, () => Save(service, current));
    }

    [Fact] public void InvalidSelectionAndPreconditionsLeaveBytesUnchanged()
    {
        using var service = new DashboardService(new Source(), FilePath);
        var baseline = service.Read("zh-CN"); byte[] bytes = File.ReadAllBytes(FilePath);
        var request = new DashboardWriteRequest(1, baseline.CatalogRevision, []);
        Failure(DashboardFailureKind.PreconditionRequired, () => service.Save("zh-CN", null, request));
        foreach (string etag in new[] { "*", "W/" + DashboardRepresentation.Create(baseline).ETag, "bad" })
            Failure(DashboardFailureKind.PreconditionInvalid, () => service.Save("zh-CN", etag, request));
        foreach (string[] selection in new[] { new[] { "core:running", "core:running" }, new[] { "core:state" }, new[] { Plugin().CardId }, new[] { new string('a', 137) } })
            Failure(DashboardFailureKind.CardInvalid, () => Save(service, baseline, selection));
        Failure(DashboardFailureKind.TooLarge, () => Save(service, baseline, Enumerable.Repeat("core:running", 513).ToArray()));
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
    }

    [Fact] public void UnavailablePositionsSurviveEditsAndAllCardsCanBeHidden()
    {
        var source = new Source { Cards = [Plugin()] };
        using var service = new DashboardService(source, FilePath);
        var baseline = service.Read("zh-CN");
        Save(service, baseline, "core:running", Plugin().CardId, "core:status", "core:history-duration");
        source.Set(); var missing = service.Read("zh-CN");
        var entry = missing.Layout.Entries[1];
        var saved = Save(service, missing, "core:history-duration");
        Assert.Equal(entry, saved.Layout.Entries[1]); Assert.True(entry.Visible);
        var hidden = Save(service, saved);
        Assert.Empty(hidden.VisibleCardIds); Assert.Equal(entry, hidden.Layout.Entries[1]);
        source.Set(Plugin());
        Assert.Equal(new[] { Plugin().CardId }, service.Read("zh-CN").VisibleCardIds);
    }

    [Fact] public void FirstHiddenRegistrationIsNotDefaultedAgain()
    {
        var source = new Source(); using var service = new DashboardService(source, FilePath);
        var baseline = service.Read("zh-CN"); Save(service, baseline, "core:history-duration", "core:running");
        source.Set(Plugin(false)); var seen = service.Read("zh-CN");
        Assert.False(seen.Layout.Entries[^1].Visible);
        source.Set(Plugin(true)); var updated = service.Read("zh-CN");
        Assert.Equal(seen.Layout.Entries, updated.Layout.Entries); Assert.Equal(seen.Layout.Revision, updated.Layout.Revision);
    }

    [Fact] public void CorruptUnknownAndExternallyChangedFilesArePreserved()
    {
        Directory.CreateDirectory(_root);
        foreach (string content in new[] { "broken", "{\"schemaVersion\":9}", "null", "{}" })
        {
            File.WriteAllText(FilePath, content); using var corrupt = new DashboardService(new Source(), FilePath);
            Failure(DashboardFailureKind.Unavailable, () => corrupt.Read("zh-CN")); Assert.Equal(content, File.ReadAllText(FilePath));
        }
        File.Delete(FilePath); using var service = new DashboardService(new Source(), FilePath);
        var baseline = service.Read("zh-CN"); var document = File.ReadAllText(FilePath);
        File.WriteAllText(FilePath, document + " ");
        Failure(DashboardFailureKind.Unavailable, () => Save(service, baseline)); Assert.Equal(document + " ", File.ReadAllText(FilePath));
        var malformed = document.Replace("\"visible\":true", "\"other\":true"); File.WriteAllText(FilePath, malformed);
        using var missingField = new DashboardService(new Source(), FilePath);
        Failure(DashboardFailureKind.Unavailable, () => missingField.Read("zh-CN")); Assert.Equal(malformed, File.ReadAllText(FilePath));
    }

    [Fact] public void InvalidCatalogMetadataCannotEnterPersistedLayout()
    {
        var source = new Source(); using var service = new DashboardService(source, FilePath);
        var baseline = service.Read("zh-CN"); var bytes = File.ReadAllBytes(FilePath);
        source.Set(Plugin() with { PluginName = "another-owner" });
        Failure(DashboardFailureKind.Unavailable, () => service.Read("zh-CN")); Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        source.Set(Plugin() with { Title = "" });
        Failure(DashboardFailureKind.Unavailable, () => service.Read("zh-CN")); Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        source.Set(Plugin()); var valid = service.Read("zh-CN");
        Assert.Equal(baseline.Layout.Revision + 1, valid.Layout.Revision); Assert.Contains(Plugin().CardId, valid.VisibleCardIds);
    }

    [Fact] public void StatusAndCombinedHistoryPreserveExistingLayoutIdentityAndRecords()
    {
        Directory.CreateDirectory(_root);
        var original = new DashboardLayout(1, Guid.NewGuid().ToString(), 7, DateTime.UtcNow,
            [new("core:running", true), new("core:history-counts", false), new("core:history-duration", true), new(Plugin().CardId, false)]);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(original, DashboardRepresentation.Json));
        var source = new Source { Cards = [Plugin()] };
        using (var service = new DashboardService(source, FilePath))
        {
            var discovered = service.Read("zh-CN");
            Assert.Equal(original.LayoutId, discovered.Layout.LayoutId);
            Assert.Equal(original.Entries, discovered.Layout.Entries.Skip(1));
            Assert.Equal("core:status", discovered.Layout.Entries[0].CardId);
            Assert.DoesNotContain(discovered.Cards, card => card.CardId == "core:history-counts");
            Failure(DashboardFailureKind.CardInvalid, () => Save(service, discovered, "core:history-counts"));
            var hidden = Save(service, discovered, "core:running");
            Assert.False(hidden.Layout.Entries.Single(e => e.CardId == "core:status").Visible);
            Assert.False(hidden.Layout.Entries.Single(e => e.CardId == "core:history-duration").Visible);
            Assert.Equal(original.Entries[1], hidden.Layout.Entries.Single(e => e.CardId == "core:history-counts"));
        }
        using var restarted = new DashboardService(source, FilePath);
        var restored = restarted.Read("zh-CN");
        Assert.Equal(new[] { "core:running" }, restored.VisibleCardIds);
        Assert.Equal(original.LayoutId, restored.Layout.LayoutId);
    }
}
