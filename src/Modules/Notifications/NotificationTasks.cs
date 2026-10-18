using System.Text;
using System.Text.Json;
using NexusPipeline.Modules.Settings.Validation;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Results;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Notifications;

internal sealed class NotificationTasks
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Action<string, string> _save;
    private readonly Func<int> _limit;
    private NotificationTaskStore _store = new();
    public string? BlockedReason { get; private set; }

    internal void Block(string reason)
    {
        lock (_gate) BlockedReason = reason;
    }

    public NotificationTasks(string path, Func<int>? limit = null, Action<string, string>? save = null)
    {
        _path = path;
        _limit = limit ?? (() => Limits.Current.MaxNotificationTasks);
        _save = save ?? ((target, content) => JsonUtil.WriteAtomic(target, content));
        Reload();
    }

    internal void Reload()
    {
        lock (_gate)
        {
            try
            {
                var candidate = File.Exists(_path)
                    ? JsonSerializer.Deserialize<NotificationTaskStore>(File.ReadAllText(_path), JsonOpts.Default)
                    : new NotificationTaskStore();
                if (candidate is null || candidate.SchemaVersion != 1 || candidate.Tasks is null
                    || candidate.Tasks.Count > 99 || candidate.Tasks.Any(task => task is null
                        || !Guid.TryParseExact(task.Id, "N", out _) || task.Configuration is null
                        || string.IsNullOrWhiteSpace(task.Name) || Encoding.UTF8.GetByteCount(task.Name) > 64
                        || task.Remark is null || Encoding.UTF8.GetByteCount(task.Remark) > 512 || !task.Configuration.HasNonNullFields()
                        || task.Type is not ("smtp" or "webhook"))
                    || candidate.Tasks.Select(task => task.Id).Distinct(StringComparer.Ordinal).Count() != candidate.Tasks.Count
                    || candidate.Tasks.Select(task => task.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != candidate.Tasks.Count)
                    throw new InvalidDataException();
                _store = candidate;
                BlockedReason = null;
            }
            catch
            {
                BlockedReason = "notification_store_invalid";
            }
        }
    }

    public IReadOnlyList<NotificationTaskView> List()
    {
        lock (_gate) return _store.Tasks.Select(View).ToArray();
    }

    public NotificationTaskView? Find(string id)
    {
        lock (_gate) return _store.Tasks.FirstOrDefault(task => task.Id == id) is { } task ? View(task) : null;
    }

    public IReadOnlyList<NotificationTask> Snapshot(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            if (BlockedReason is not null) return [];
            var targets = ids.ToHashSet(StringComparer.Ordinal);
            return _store.Tasks.Where(task => targets.Contains(task.Id)).Select(task => task.Clone()).ToArray();
        }
    }

    public string? ValidateTargets(IEnumerable<string>? ids, IEnumerable<string>? previous = null)
    {
        lock (_gate)
        {
            if (BlockedReason is not null)
                return ids is not null && ids.All(id => (previous ?? []).Contains(id, StringComparer.Ordinal)) ? null : BlockedReason;
            if (ids is null) return "notification_targets_invalid";
            var selected = ids.ToArray();
            if (selected.Length > 99) return "notification_targets_invalid";
            var known = _store.Tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
            known.UnionWith(previous ?? []);
            return selected.Any(id => string.IsNullOrWhiteSpace(id) || !known.Contains(id)) ? "notification_target_not_found" : null;
        }
    }

    public OperationResult<NotificationTaskView> Save(string? id, NotificationTaskInput input)
    {
        lock (_gate)
        {
            if (BlockedReason is not null) return Fail<NotificationTaskView>(BlockedReason, OperationErrorKind.Unavailable);
            NotificationTask? old = id is null ? null : _store.Tasks.FirstOrDefault(task => task.Id == id);
            if (id is not null && old is null) return Fail<NotificationTaskView>("notification_not_found", OperationErrorKind.NotFound);
            if (old is null && _store.Tasks.Count >= Math.Min(99, _limit())) return Fail<NotificationTaskView>("notification_limit_reached");
            if (input is null || input.Configuration is null || !input.Configuration.HasNonNullFields() || input.Secrets is null || input.Name is null || input.Remark is null)
                return Fail<NotificationTaskView>("notification_fields_invalid");
            string name = input.Name.Trim();
            if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > 64 || Encoding.UTF8.GetByteCount(input.Remark.Trim()) > 512)
                return Fail<NotificationTaskView>("notification_name_or_remark_invalid");
            if (_store.Tasks.Any(task => task.Id != id && string.Equals(task.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Fail<NotificationTaskView>("notification_name_conflict", OperationErrorKind.Conflict);
            if (input.Type is not ("smtp" or "webhook") || old is not null && old.Type != input.Type)
                return Fail<NotificationTaskView>("notification_type_invalid");
            var candidate = new NotificationTask
            {
                Id = old?.Id ?? Guid.NewGuid().ToString("N"), Name = name, Remark = input.Remark.Trim(),
                Type = input.Type, Enabled = input.Enabled, Configuration = input.Configuration.Clone()
            };
            try
            {
                if (input.Secrets.Keys.Any(key => !NotificationChannelSettings.SecretNames.Contains(key)))
                    return Fail<NotificationTaskView>("notification_secret_invalid");
                foreach (string key in NotificationChannelSettings.SecretNames)
                {
                    if (candidate.Configuration.GetSecret(key) != "") return Fail<NotificationTaskView>("notification_secret_action_required");
                    var change = input.Secrets.GetValueOrDefault(key) ?? new NotificationSecretInput("keep");
                    string value = change.Action switch
                    {
                        "keep" => old?.Configuration.GetSecret(key) ?? "",
                        "clear" => "",
                        "replace" when !string.IsNullOrWhiteSpace(change.Value) => SecretStore.Encrypt(change.Value),
                        _ => throw new ArgumentException()
                    };
                    candidate.Configuration.SetSecret(key, value);
                }
                string? error = ValidateConfiguration(candidate, requireComplete: candidate.Enabled);
                if (error is not null) return Fail<NotificationTaskView>(error);
                var tasks = _store.Tasks.Select(task => task.Clone()).ToList();
                if (old is null) tasks.Add(candidate);
                else tasks[tasks.FindIndex(task => task.Id == old.Id)] = candidate;
                Persist(tasks);
                return OperationResult<NotificationTaskView>.Ok(View(candidate));
            }
            catch (ArgumentException) { return Fail<NotificationTaskView>("notification_secret_invalid"); }
            catch { return Fail<NotificationTaskView>("notification_save_failed", OperationErrorKind.Internal); }
        }
    }

    public OperationResult<bool> Delete(string id)
    {
        lock (_gate)
        {
            if (BlockedReason is not null) return Fail<bool>(BlockedReason, OperationErrorKind.Unavailable);
            if (!_store.Tasks.Any(task => task.Id == id)) return Fail<bool>("notification_not_found", OperationErrorKind.NotFound);
            try { Persist(_store.Tasks.Where(task => task.Id != id).Select(task => task.Clone()).ToList()); return OperationResult.Ok(); }
            catch { return Fail<bool>("notification_save_failed", OperationErrorKind.Internal); }
        }
    }

    public OperationResult<bool> Reorder(IReadOnlyList<string>? ids)
    {
        lock (_gate)
        {
            if (BlockedReason is not null) return Fail<bool>(BlockedReason, OperationErrorKind.Unavailable);
            if (ids is null || ids.Count != _store.Tasks.Count || ids.Distinct().Count() != ids.Count
                || ids.Any(id => !_store.Tasks.Any(task => task.Id == id))) return Fail<bool>("notification_order_invalid");
            try { Persist(ids.Select(id => _store.Tasks.Single(task => task.Id == id).Clone()).ToList()); return OperationResult.Ok(); }
            catch { return Fail<bool>("notification_save_failed", OperationErrorKind.Internal); }
        }
    }

    private void Persist(List<NotificationTask> tasks)
    {
        var candidate = new NotificationTaskStore { Tasks = tasks };
        _save(_path, JsonSerializer.Serialize(candidate, JsonOpts.Indented));
        _store = candidate;
    }

    internal static string? ValidateConfiguration(NotificationTask task, bool requireComplete = true)
    {
        var config = task.Configuration;
        if (!config.HasNonNullFields()) return "notification_fields_invalid";
        if (config.Timeout is < 1 or > 300) return "notification_timeout_invalid";
        if (task.Type == "smtp")
        {
            if (config.SmtpPort is < 1 or > 65535 || config.SmtpSecure is not ("auto" or "ssl" or "starttls" or "none"))
                return "notification_smtp_invalid";
            if (config.SmtpTo.Length > 4096 || !string.IsNullOrWhiteSpace(config.SmtpTo) && SmtpSender.ValidateRecipients(config.SmtpTo) is not null)
                return "notification_recipients_invalid";
            if (!string.IsNullOrWhiteSpace(config.SmtpFrom) && !MimeKit.MailboxAddress.TryParse(config.SmtpFrom, out _))
                return "notification_smtp_invalid";
            if (requireComplete && (string.IsNullOrWhiteSpace(config.SmtpHost) || string.IsNullOrWhiteSpace(config.SmtpUser)
                || string.IsNullOrWhiteSpace(config.SmtpPassword) || string.IsNullOrWhiteSpace(config.SmtpTo)))
                return "notification_configuration_incomplete";
        }
        else
        {
            if (!NotificationChannelSettings.WebhookTypes.Contains(config.WebhookType)) return "notification_webhook_type_invalid";
            if (requireComplete && (string.IsNullOrWhiteSpace(config.WebhookUrl)
                || config.WebhookType == "generic" && string.IsNullOrWhiteSpace(config.WebhookTemplate)))
                return "notification_configuration_incomplete";
            if (config.WebhookType == "generic" && !string.IsNullOrWhiteSpace(config.WebhookTemplate))
            {
                try { using var document = JsonDocument.Parse(WebhookSender.BuildGenericBody("notification", config.WebhookTemplate, null)); }
                catch (JsonException) { return "notification_template_invalid"; }
            }
            if (!string.IsNullOrEmpty(config.WebhookUrl) && SecretStore.TryDecrypt(config.WebhookUrl, out string? url)
                && (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")))
                return "notification_webhook_url_invalid";
        }
        if (requireComplete && NotificationChannelSettings.SecretNames.Any(key => !SecretStore.TryDecrypt(config.GetSecret(key), out _)))
            return "notification_secret_unavailable";
        return null;
    }

    private static NotificationTaskView View(NotificationTask task)
    {
        var config = task.Configuration.Clone();
        var secrets = NotificationChannelSettings.SecretNames.ToDictionary(key => key, key => config.GetSecret(key).Length != 0);
        foreach (string key in NotificationChannelSettings.SecretNames) config.SetSecret(key, "");
        string? error = ValidateConfiguration(task);
        return new(task.Id, task.Name, task.Remark, task.Type, task.Enabled, error is null, error, config, secrets);
    }

    private static OperationResult<T> Fail<T>(string code, OperationErrorKind kind = OperationErrorKind.Validation) =>
        OperationResult<T>.Failure(code, code, kind);
}
