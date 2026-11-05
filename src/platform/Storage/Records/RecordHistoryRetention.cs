using System.Globalization;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Records;

[SupportedOSPlatform("windows")]
public sealed class RecordHistoryRetention
{
    private readonly StateDatabase _database;
    private readonly ContentRetirementService _retirement;
    private readonly TimeProvider _clock;
    private Guid? _after;
    private DateOnly? _nextSweep;
    private long _settingsRevision = -1;
    private Guid? _resumeAfter;
    private DateOnly? _resumeDay;
    private bool _resumeComplete;
    public string? FailureCode { get; private set; }

    public RecordHistoryRetention(StateDatabase database, ContentRetirementService retirement, TimeProvider? clock = null)
    { _database = database; _retirement = retirement; _clock = clock ?? TimeProvider.System; }

    public async Task RunAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _clock);
        try
        {
            do
            {
                stopping.ThrowIfCancellationRequested();
                try { Tick(); }
                catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or System.Text.Json.JsonException)
                { FailureCode = "record_retention_reconciliation_required"; }
            } while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    internal void Tick()
    {
        var saved = new HostSettingsStore(_database).Read(); var now = _clock.GetUtcNow();
        var localDate = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Date;
        if (_settingsRevision != saved.Revision)
        { _settingsRevision = saved.Revision; _after = null; _nextSweep = null; _resumeDay = null; }
        var cutoffDate = localDate.AddDays(1 - saved.Values.HistoryRetentionDays);
        var cutoff = new DateTimeOffset(cutoffDate, TimeZoneInfo.Local.GetUtcOffset(cutoffDate));
        var today = DateOnly.FromDateTime(localDate);
        if (_resumeDay != today) { _resumeDay = today; _resumeAfter = null; _resumeComplete = false; }
        if (!_resumeComplete)
        {
            var resumed = _retirement.ResumeAutomatic(_resumeAfter, saved.Revision, cutoff);
            if (resumed is not null) { _resumeAfter = resumed; FailureCode = null; return; }
            _resumeComplete = true;
        }
        if (_nextSweep is { } next && today < next) return;
        Guid? run;
        using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT r.run_id FROM run_records r JOIN execution_start_sources s ON s.run_id=r.run_id AND s.attempt_ordinal=1 " +
                "JOIN operations o ON o.operation_id=s.operation_id WHERE r.history_retired_at_utc_ticks IS NULL AND r.run_id>$after " +
                "AND julianday(o.created_at)<julianday($cutoff) ORDER BY r.run_id LIMIT 1;";
            command.Parameters.AddWithValue("$after", _after?.ToString("D") ?? "");
            command.Parameters.AddWithValue("$cutoff", cutoff.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            run = command.ExecuteScalar() is string id ? Guid.ParseExact(id, "D") : null;
        }
        if (run is null)
        { _after = null; _nextSweep = DateOnly.FromDateTime(localDate).AddDays(1); return; }
        try
        {
            foreach (var images in new[] { false, true })
            {
                _resumeComplete = false; _resumeAfter = null;
                var page = images ? _retirement.RetireImagesPage(Guid.NewGuid(), "host:history-retention", run.Value) :
                    _retirement.RetireLogsPage(Guid.NewGuid(), "host:history-retention", run.Value);
                if (page is null) continue;
                if (!_retirement.CompleteCleanup(page)) throw new IOException("record_retirement_cleanup_pending");
                FailureCode = null; return;
            }
            if (_retirement.RetireEmptyHistory(run.Value, saved.Revision, now)) _after = run;
            FailureCode = null;
        }
        catch (IOException error) when (error.Message is "record_retirement_admission_blocked" or "record_retirement_run_pending" or
            "record_retirement_notification_pending" or "record_retirement_recovery_pending" or "record_retirement_recovery_changed" or "record_retirement_settings_changed")
        { _after = run; FailureCode = error.Message; }
    }
}
