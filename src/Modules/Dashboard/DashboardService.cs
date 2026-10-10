using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NexusPipeline.Modules.Dashboard;

internal sealed class DashboardService : IDisposable
{
    private readonly object _sync = new();
    private readonly IDashboardCardSource _source;
    private readonly DashboardLayoutStore _store;
    private readonly TimeProvider _clock;
    private readonly Action<DashboardChange>? _publish;
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private DashboardLayout? _layout;
    private string? _catalogRevision;
    private bool _loaded;
    private bool _disposed;

    internal DashboardService(IDashboardCardSource source, string path, Action<string, string>? save = null,
        Action<DashboardChange>? publish = null, TimeProvider? clock = null)
    {
        _source = source;
        _store = new(path, save);
        _clock = clock ?? TimeProvider.System;
        _publish = publish;
        source.Changed += OnCatalogChanged;
    }

    public DashboardSnapshot Read(string locale)
    {
        List<DashboardChange> changes = [];
        DashboardSnapshot result;
        lock (_sync) result = ReadLocked(locale, changes);
        Publish(changes);
        return result;
    }

    internal DashboardSnapshot Save(string locale, string? ifMatch, DashboardWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(ifMatch)) throw new DashboardFailure(DashboardFailureKind.PreconditionRequired);
        if (!Regex.IsMatch(ifMatch, "^\"dashboard-sha256-[0-9a-f]{64}\"$")) throw new DashboardFailure(DashboardFailureKind.PreconditionInvalid);
        if (request.SchemaVersion != 1 || request.CatalogRevision is null || !Regex.IsMatch(request.CatalogRevision, "^[0-9a-f]{64}$") || request.VisibleCardIds is null)
            throw new DashboardFailure(DashboardFailureKind.Invalid);
        if (request.VisibleCardIds.Length > DashboardLayoutStore.MaximumEntries) throw new DashboardFailure(DashboardFailureKind.TooLarge);
        List<DashboardChange> changes = [];
        DashboardSnapshot result;
        try
        {
            lock (_sync)
            {
                var current = ReadLocked(locale, changes);
                if (request.CatalogRevision != current.CatalogRevision) throw new DashboardFailure(DashboardFailureKind.CatalogChanged);
                if (ifMatch != DashboardRepresentation.Create(current).ETag) throw new DashboardFailure(DashboardFailureKind.Conflict);
                var available = current.Cards.Select(c => c.CardId).ToHashSet(StringComparer.Ordinal);
                var selected = request.VisibleCardIds;
                if (selected.Any(id => !DashboardLayoutStore.ValidId(id) || !available.Contains(id)) || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
                    throw new DashboardFailure(DashboardFailureKind.CardInvalid);
                var selection = selected.ToHashSet(StringComparer.Ordinal);
                var sequence = selected.Select(id => new DashboardEntry(id, true))
                    .Concat(current.Layout.Entries.Where(e => available.Contains(e.CardId) && !selection.Contains(e.CardId)).Select(e => e with { Visible = false })).ToArray();
                int index = 0;
                var entries = current.Layout.Entries.Select(e => available.Contains(e.CardId) ? sequence[index++] : e).ToArray();
                EnsureCatalogUnchanged(locale, current.CatalogRevision);
                if (!entries.SequenceEqual(current.Layout.Entries))
                {
                    Commit(Next(current.Layout, entries), changes);
                }
                result = Snapshot(current.CatalogRevision, current.Cards, _layout!);
            }
        }
        finally { Publish(changes); }
        return result;
    }

    private DashboardSnapshot ReadLocked(string locale, List<DashboardChange> changes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_loaded) { _layout = _store.Load(); _loaded = true; }
        else _store.EnsureUnchanged();
        var (revision, cards) = Catalog(locale);
        var entries = _layout?.Entries.ToList() ?? [];
        var known = entries.Select(e => e.CardId).ToHashSet(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            if (!known.Add(card.CardId)) continue;
            var entry = new DashboardEntry(card.CardId, card.DefaultVisible);
            if (card.CardId == "core:status") entries.Insert(0, entry);
            else entries.Add(entry);
        }
        if (entries.Count > DashboardLayoutStore.MaximumEntries) throw new DashboardFailure(DashboardFailureKind.TooLarge);
        if (_layout is null || entries.Count != _layout.Entries.Length)
        {
            EnsureCatalogUnchanged(locale, revision);
            var next = _layout is null ? new DashboardLayout(1, Guid.NewGuid().ToString(), 1, _clock.GetUtcNow().UtcDateTime, entries.ToArray()) : Next(_layout, entries.ToArray());
            Commit(next, changes);
        }
        if (_catalogRevision != revision)
        {
            _catalogRevision = revision;
            changes.Add(new("catalog", null, null, revision));
        }
        return Snapshot(revision, cards, _layout!);
    }

    private (string Revision, DashboardCard[] Cards) Catalog(string locale)
    {
        bool english = locale == "en-US";
        DashboardCard[] core = [
            new("core:status", "core", null, "status", english ? "System status" : "一切准备就绪",
                english ? "Shows whether the system is idle or running tasks, with a summary of active work." : "显示系统空闲或任务进行中的状态，并汇总当前活动任务数量。", true, 0),
            new("core:running", "core", null, "running", english ? "Running" : "正在运行",
                english ? "Lists active scripts and queues, their current steps, retry progress and execution status." : "查看正在执行的脚本和队列，以及当前步骤、重试进度和运行状态。", true, 100),
            new("core:history-duration", "core", null, "history-duration", english ? "Run history" : "运行历史统计",
                english ? "Shows the last seven days of run duration and total, successful and abnormal run counts. Both charts move and hide together." : "汇总最近七天的运行时长及总次数、成功次数和异常次数；两张图表作为一个整体排序、显示或隐藏。", true, 200)];
        var supplied = _source.Read(locale);
        if (supplied.Cards.Any(c => c is null || c.SourceKind != "plugin" || c.PluginName is null
            || c.CardId != $"plugin:{c.PluginName}:{c.LocalId}" || c.Title is null || c.Title.Length is < 1 or > 200
            || c.Description is null || c.Description.Length > 2000))
            throw new DashboardFailure(DashboardFailureKind.Unavailable);
        var cards = core.Concat(supplied.Cards).OrderBy(c => c.DefaultOrder).ThenBy(c => c.CardId, StringComparer.Ordinal).ToArray();
        if (cards.Length > DashboardLayoutStore.MaximumEntries) throw new DashboardFailure(DashboardFailureKind.TooLarge);
        if (cards.Any(c => !DashboardLayoutStore.ValidId(c.CardId)) || cards.Select(c => c.CardId).Distinct(StringComparer.Ordinal).Count() != cards.Length)
            throw new DashboardFailure(DashboardFailureKind.Unavailable);
        return (CatalogToken(supplied.Revision), cards);
    }

    private string CatalogToken(string sourceRevision) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_instance + ":" + sourceRevision)));
    private void EnsureCatalogUnchanged(string locale, string revision)
    {
        if (Catalog(locale).Revision != revision) throw new DashboardFailure(DashboardFailureKind.CatalogChanged);
    }
    private DashboardLayout Next(DashboardLayout current, DashboardEntry[] entries)
    {
        if (current.Revision == DashboardLayoutStore.MaximumRevision) throw new DashboardFailure(DashboardFailureKind.Unavailable);
        return current with { Entries = entries, Revision = current.Revision + 1, UpdatedAtUtc = _clock.GetUtcNow().UtcDateTime };
    }
    private void Commit(DashboardLayout next, List<DashboardChange> changes)
    {
        _store.Save(next);
        _layout = next;
        changes.Add(new("layout", next.LayoutId, next.Revision, null));
    }
    private static DashboardSnapshot Snapshot(string revision, IReadOnlyList<DashboardCard> cards, DashboardLayout layout)
    {
        var available = cards.Select(c => c.CardId).ToHashSet(StringComparer.Ordinal);
        return new(1, revision, cards.ToArray(), layout with { Entries = layout.Entries.ToArray() },
            layout.Entries.Where(e => e.Visible && available.Contains(e.CardId)).Select(e => e.CardId).ToArray());
    }
    private void OnCatalogChanged()
    {
        if (!_disposed) _publish?.Invoke(new("catalog", null, null, CatalogToken(_source.Read("zh-CN").Revision)));
    }
    private void Publish(IEnumerable<DashboardChange> changes)
    {
        foreach (var change in changes) _publish?.Invoke(change);
    }
    public void Dispose() { _disposed = true; _source.Changed -= OnCatalogChanged; }
}
