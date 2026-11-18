namespace NexusPipeline.Platform.Storage.Sqlite;

[Flags]
internal enum ControlStatisticsFields
{
    Pending = 1, Attempts = 2, Runs = 4, Catalog = 8, Settings = 16,
    All = Pending | Attempts | Runs | Catalog | Settings
}

internal readonly record struct ControlStatisticsVersion(long Revision, long Pending, long Attempts, long Runs,
    long Catalog, long Settings)
{
    internal ControlStatisticsVersion Advance(ControlStatisticsFields fields)
    {
        var next = Revision + 1;
        return new(next,
            fields.HasFlag(ControlStatisticsFields.Pending) ? next : Pending,
            fields.HasFlag(ControlStatisticsFields.Attempts) ? next : Attempts,
            fields.HasFlag(ControlStatisticsFields.Runs) ? next : Runs,
            fields.HasFlag(ControlStatisticsFields.Catalog) ? next : Catalog,
            fields.HasFlag(ControlStatisticsFields.Settings) ? next : Settings);
    }

    internal ControlStatisticsFields ChangedSince(ControlStatisticsVersion old)
    {
        var fields = (ControlStatisticsFields)0;
        if (Pending != old.Pending) fields |= ControlStatisticsFields.Pending;
        if (Attempts != old.Attempts) fields |= ControlStatisticsFields.Attempts;
        if (Runs != old.Runs) fields |= ControlStatisticsFields.Runs;
        if (Catalog != old.Catalog) fields |= ControlStatisticsFields.Catalog;
        if (Settings != old.Settings) fields |= ControlStatisticsFields.Settings;
        return fields;
    }
}
