using System.Collections.Immutable;
using NexusPipeline.Core.Automation;

namespace NexusPipeline.Platform.Storage.Automation;

internal static class AssetDefinitionRevisions
{
    internal static IReadOnlyList<AssetPreparationDefsDefinitionRevision>? Capture(AutomationChanges definitions)
    {
        var expected = new List<AssetPreparationDefsDefinitionRevision>();
        Add("root", definitions.Roots.Select(item => (item.Id, item.Revision)));
        Add("script", definitions.Scripts.Select(item => (item.Id, item.Revision)));
        Add("user", definitions.Users.Select(item => (item.Id, item.Revision)));
        Add("binding", definitions.Bindings.Select(item => (item.Id, item.Revision)));
        Add("queue", definitions.Queues.Select(item => (item.Id, item.Revision)));
        Add("schedule", definitions.Schedules.Select(item => (item.Id, item.Revision)));
        return expected.Count == 0 ? null : expected;

        void Add(string kind, IEnumerable<(Guid Id, long Revision)> items)
        {
            foreach (var (id, revision) in items.Where(item => item.Revision > 0).OrderBy(item => item.Id))
                expected.Add(new() { Kind = kind, Id = id.ToString("D"), Revision = revision });
        }
    }

    internal static ParsedAutomationBundle Restore(ParsedAutomationBundle bundle, IReadOnlyList<AssetPreparationDefsDefinitionRevision>? saved)
    {
        // Portable inputs carry no entity revisions. The plan carries only those approved updates that need one.
        if (saved is null) return bundle;
        if (bundle.IsScriptTemplate) throw Invalid();
        var expected = new Dictionary<(string Kind, Guid Id), long>();
        foreach (var item in saved)
        {
            if (!Guid.TryParseExact(item.Id, "D", out var id) || !expected.TryAdd((item.Kind, id), item.Revision)) throw Invalid();
        }
        var definitions = bundle.Definitions;
        var restored = definitions with
        {
            Roots = definitions.Roots.Select(item => expected.Remove(("root", item.Id), out var revision) ? item with { Revision = revision } : item).ToImmutableArray(),
            Scripts = definitions.Scripts.Select(item => expected.Remove(("script", item.Id), out var revision) ? item with { Revision = revision } : item).ToImmutableArray(),
            Users = definitions.Users.Select(item => expected.Remove(("user", item.Id), out var revision) ? item with { Revision = revision } : item).ToImmutableArray(),
            Bindings = definitions.Bindings.Select(item => expected.Remove(("binding", item.Id), out var revision) ? item with { Revision = revision } : item).ToImmutableArray(),
            Queues = definitions.Queues.Select(item => expected.Remove(("queue", item.Id), out var revision) ? item with { Revision = revision } : item).ToImmutableArray(),
            Schedules = definitions.Schedules.Select(item => expected.Remove(("schedule", item.Id), out var revision) ? item with { Revision = revision } : item).ToImmutableArray(),
        };
        if (expected.Count != 0) throw Invalid();
        return bundle with { Definitions = restored };
    }

    private static IOException Invalid() => new("asset_preparation_definition_revision_unbound");
}
