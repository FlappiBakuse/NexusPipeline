using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace NexusPipeline.Core.Notifications;

public static class NotificationTaskPolicy
{
    public const long MaximumRevision = 9007199254740991;

    public static void Validate(NotificationTaskCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Revision is < 0 or > MaximumRevision || catalog.MaximumTasks is < 1 or > 99 ||
            catalog.Tasks.IsDefault || catalog.Tasks.Length > 99) Reject("notification_catalog_invalid");
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in catalog.Tasks)
        {
            if (task is null || task.Id == Guid.Empty || task.Revision is < 1 or > MaximumRevision || !ids.Add(task.Id) ||
                !Text(task.Name, 64, required: true) || task.Name.Trim() != task.Name || !names.Add(task.Name) ||
                !Text(task.Remark, 512) || task.Remark.Trim() != task.Remark || !Enum.IsDefined(task.Kind))
                Reject("notification_task_invalid");
            ValidateConfiguration(task.Configuration);
        }
    }

    public static NotificationTaskCatalog Save(NotificationTaskCatalog current, long expectedCatalogRevision,
        Guid id, long expectedTaskRevision, NotificationTaskInput input)
    {
        RequireRevision(current, expectedCatalogRevision);
        if (id == Guid.Empty || input is null || input.Name is null || input.Remark is null)
            Reject("notification_task_invalid");
        var prior = current.Tasks.SingleOrDefault(task => task.Id == id);
        if (prior is null)
        {
            if (expectedTaskRevision != 0) Reject("notification_not_found");
            if (current.Tasks.Length >= current.MaximumTasks) Reject("notification_limit_reached");
        }
        else
        {
            if (prior.Revision != expectedTaskRevision) Reject("notification_revision_conflict");
            if (prior.Kind != input.Kind) Reject("notification_type_immutable");
        }
        var candidate = new NotificationTaskDefinition(id, checked(expectedTaskRevision + 1), input.Name.Trim(),
            input.Remark.Trim(), input.Kind, input.IsEnabled, input.Configuration);
        var items = prior is null ? current.Tasks.Add(candidate) : current.Tasks.Replace(prior, candidate);
        var next = current with { Revision = checked(current.Revision + 1), Tasks = items };
        Validate(next);
        return next;
    }

    public static NotificationTaskCatalog Remove(NotificationTaskCatalog current, long expectedCatalogRevision,
        Guid id, long expectedTaskRevision)
    {
        RequireRevision(current, expectedCatalogRevision);
        var prior = current.Tasks.SingleOrDefault(task => task.Id == id) ?? throw new NotificationTaskException("notification_not_found");
        if (prior.Revision != expectedTaskRevision) Reject("notification_revision_conflict");
        return current with { Revision = checked(current.Revision + 1), Tasks = current.Tasks.Remove(prior) };
    }

    public static NotificationTaskCatalog Reorder(NotificationTaskCatalog current, long expectedCatalogRevision, ImmutableArray<Guid> order)
    {
        RequireRevision(current, expectedCatalogRevision);
        if (order.IsDefault || order.Length != current.Tasks.Length || order.Distinct().Count() != order.Length ||
            order.Any(id => !current.Tasks.Any(task => task.Id == id))) Reject("notification_order_invalid");
        return current with { Revision = checked(current.Revision + 1),
            Tasks = order.Select(id => current.Tasks.Single(task => task.Id == id)).ToImmutableArray() };
    }

    public static NotificationTaskCatalog SetLimit(NotificationTaskCatalog current, long expectedCatalogRevision, int maximum)
    {
        RequireRevision(current, expectedCatalogRevision);
        var next = current with { Revision = checked(current.Revision + 1), MaximumTasks = maximum };
        Validate(next);
        return next;
    }

    public static NotificationTaskSelection ValidateSelection(NotificationTaskCatalog catalog, NotificationTaskSelection selection,
        NotificationTaskSelection? previous = null)
    {
        Validate(catalog);
        return ValidateSelectionIds(catalog.Tasks.Select(task => task.Id).ToHashSet(), selection, previous);
    }

    public static NotificationTaskSelection ValidateSelectionIds(IReadOnlySet<Guid> knownTaskIds,
        NotificationTaskSelection selection, NotificationTaskSelection? previous = null)
    {
        ArgumentNullException.ThrowIfNull(knownTaskIds);
        if (knownTaskIds.Count > 99 || knownTaskIds.Contains(Guid.Empty)) Reject("notification_targets_invalid");
        if (selection is null || selection.TaskIds.IsDefault || selection.TaskIds.Length > 99 ||
            selection.TaskIds.Contains(Guid.Empty) || selection.TaskIds.Distinct().Count() != selection.TaskIds.Length)
            Reject("notification_targets_invalid");
        var known = knownTaskIds.ToHashSet();
        if (previous is not null)
        {
            if (previous.TaskIds.IsDefault || previous.TaskIds.Length > 99 || previous.TaskIds.Contains(Guid.Empty))
                Reject("notification_targets_invalid");
            known.UnionWith(previous.TaskIds);
        }
        if (selection.TaskIds.Any(id => !known.Contains(id))) Reject("notification_target_not_found");
        return selection;
    }

    private static void ValidateConfiguration(NotificationChannelConfiguration configuration)
    {
        if (configuration is null || configuration.TimeoutSeconds is < 1 or > 300 || !Enum.IsDefined(configuration.WebhookKind) ||
            !Enum.IsDefined(configuration.SmtpSecurity) || configuration.SmtpPort is < 1 or > 65535 ||
            !Text(configuration.WebhookTemplate, 65536) || !Text(configuration.FeishuAppId, 1024) ||
            !Text(configuration.SlackChannelId, 1024) || !Text(configuration.SmtpHost, 1024) ||
            !Text(configuration.SmtpUser, 1024) || !Text(configuration.SmtpFrom, 1024) ||
            !Text(configuration.SmtpTo, 4096) || !Text(configuration.SmtpSubjectPrefix, 1024))
            Reject("notification_configuration_invalid");
    }

    private static void RequireRevision(NotificationTaskCatalog current, long expected)
    {
        Validate(current);
        if (current.Revision != expected || current.Revision == MaximumRevision) Reject("notification_revision_conflict");
    }

    private static bool Text(string? value, int maximumBytes, bool required = false) => value is not null &&
        (!required || !string.IsNullOrWhiteSpace(value)) && !value.Contains('\0') && Encoding.UTF8.GetByteCount(value) <= maximumBytes;

    [DoesNotReturn]
    private static void Reject(string code) => throw new NotificationTaskException(code);
}
