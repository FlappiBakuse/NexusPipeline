using System.ComponentModel;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services;

namespace NexusPipeline.Mcp;

/// <summary>MCP 脚本写入 DTO。用户绑定通过全局用户命令管理。</summary>
internal sealed class McpScriptInput
{
    [Description("脚本实例名称。")]
    public string Name { get; set; } = "";

    [Description("专项插件名；空字符串表示通用脚本。")]
    public string PluginType { get; set; } = "";

    public string RootPath { get; set; } = "";

    public string MainExe { get; set; } = "";

    public string Args { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string LogPath { get; set; } = "";

    public bool LaunchGame { get; set; }

    public string GameMode { get; set; } = "";

    public string GameExe { get; set; } = "";

    public string GameArgs { get; set; } = "";

    public int GameWaitSeconds { get; set; } = 30;

    public bool ForceCloseGame { get; set; }

    public int MaxAttempts { get; set; } = 3;

    public int LogStallTimeoutMinutes { get; set; } = 5;

    public int TotalTimeoutMinutes { get; set; } = 120;

    public string SuccessKeywords { get; set; } = "";

    public string FailureKeywords { get; set; } = "";

    public bool JudgeScriptEnabled { get; set; }

    public string JudgeScriptLanguage { get; set; } = "";

    public string JudgeScript { get; set; } = "";

    public bool NotifyEnabled { get; set; }

    public bool AutoUpdateConfig { get; set; } = true;

    public ScriptInstance ToModel()
    {
        return new ScriptInstance
        {
            Name = Name.Trim(),
            PluginType = PluginType.Trim(),
            RootPath = RootPath.Trim(),
            MainExe = MainExe.Trim(),
            Args = Args,
            ConfigPath = ConfigPath.Trim(),
            LogPath = LogPath.Trim(),
            LaunchGame = LaunchGame,
            GameMode = GameMode.Trim(),
            GameExe = GameExe.Trim(),
            GameArgs = GameArgs,
            GameWaitSeconds = GameWaitSeconds,
            ForceCloseGame = ForceCloseGame,
            MaxAttempts = MaxAttempts,
            LogStallTimeoutMinutes = LogStallTimeoutMinutes,
            TotalTimeoutMinutes = TotalTimeoutMinutes,
            SuccessKeywords = SuccessKeywords,
            FailureKeywords = FailureKeywords,
            JudgeScriptEnabled = JudgeScriptEnabled,
            JudgeScriptLanguage = JudgeScriptLanguage.Trim(),
            JudgeScript = JudgeScript,
            NotifyEnabled = NotifyEnabled,
            AutoUpdateConfig = AutoUpdateConfig,
        };
    }
}

/// <summary>MCP 全局用户写入 DTO。</summary>
internal sealed class McpUserInput
{
    public string Name { get; set; } = "";

    public string Remark { get; set; } = "";
}

/// <summary>MCP 用户绑定写入 DTO；脚本引用既可填稳定 ID，也可填唯一名称。</summary>
internal sealed class McpBindingInput
{
    public string ScriptInstanceId { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public string PreRunScript { get; set; } = "";

    public bool PreRunOnceOnly { get; set; }

    public string PostRunScript { get; set; } = "";

    public bool PostRunOnFinalOnly { get; set; }

    public bool NotifyEnabled { get; set; } = true;

    public string SmtpTo { get; set; } = "";

    public int RunDays { get; set; } = -1;

    public UserScriptBinding ToModel(string scriptId)
    {
        return new UserScriptBinding
        {
            ScriptInstanceId = scriptId,
            Enabled = Enabled,
            PreRunScript = PreRunScript.Trim(),
            PreRunOnceOnly = PreRunOnceOnly,
            PostRunScript = PostRunScript.Trim(),
            PostRunOnFinalOnly = PostRunOnFinalOnly,
            NotifyEnabled = NotifyEnabled,
            SmtpTo = SmtpTo.Trim(),
            RunDays = RunDays,
        };
    }
}

/// <summary>工具输出统一 envelope 的声明类型，真实 data 仍按具体工具返回。</summary>
internal sealed class McpToolEnvelope
{
    public bool Ok { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public IReadOnlyList<string>? Candidates { get; set; }

    public object? Data { get; set; }
}

internal sealed class McpRunView
{
    public string Id { get; set; } = "";

    public string Kind { get; set; } = "";

    public string TargetId { get; set; } = "";

    public string TargetName { get; set; } = "";

    public string Mode { get; set; } = "";

    public string Status { get; set; } = "";

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public int TotalTasks { get; set; }

    public int DoneTasks { get; set; }

    public string CurrentScriptName { get; set; } = "";

    public string CurrentStatus { get; set; } = "";

    public int CurrentAttempt { get; set; }

    public int CurrentMaxAttempts { get; set; }

    public string PersistenceWarning { get; set; } = "";

    public List<RunRecord> Records { get; set; } = new();

    public List<string> LogTail { get; set; } = new();

    public static McpRunView From(RunningExecutionSnapshot snapshot, bool includeRecords = true)
    {
        return new McpRunView
        {
            Id = snapshot.Id,
            Kind = snapshot.Kind,
            TargetId = snapshot.TargetId,
            TargetName = snapshot.TargetName,
            Mode = snapshot.Mode,
            Status = snapshot.Status,
            StartedAt = snapshot.StartedAt,
            FinishedAt = snapshot.FinishedAt,
            TotalTasks = snapshot.TotalTasks,
            DoneTasks = snapshot.DoneTasks,
            CurrentScriptName = snapshot.CurrentScriptName,
            CurrentStatus = snapshot.CurrentStatus,
            CurrentAttempt = snapshot.CurrentAttempt,
            CurrentMaxAttempts = snapshot.CurrentMaxAttempts,
            PersistenceWarning = snapshot.PersistenceWarning,
            Records = includeRecords ? snapshot.Records.Select(item => item.Clone()).ToList() : new List<RunRecord>(),
            LogTail = snapshot.LogTail.ToList(),
        };
    }
}

internal static class McpViews
{
    public static object Script(ScriptInstance script, IReadOnlyList<NexusUser> users)
    {
        return new
        {
            script.Id,
            script.Name,
            script.Index,
            script.PluginType,
            script.RootPath,
            script.MainExe,
            script.Args,
            script.ConfigPath,
            script.LogPath,
            script.LaunchGame,
            script.GameMode,
            script.GameExe,
            script.GameArgs,
            script.GameWaitSeconds,
            script.ForceCloseGame,
            script.MaxAttempts,
            script.LogStallTimeoutMinutes,
            script.TotalTimeoutMinutes,
            script.SuccessKeywords,
            script.FailureKeywords,
            script.JudgeScriptEnabled,
            script.JudgeScriptLanguage,
            script.JudgeScript,
            script.NotifyEnabled,
            script.AutoUpdateConfig,
            boundUsers = users
                .OrderBy(user => user.Index)
                .Select(user => new
                {
                    user.Id,
                    user.Name,
                    binding = user.Bindings
                        .Where(binding => string.Equals(binding.ScriptInstanceId, script.Id, StringComparison.Ordinal))
                        .Select(binding => Binding(binding, user))
                        .FirstOrDefault(),
                })
                .Where(item => item.binding is not null)
                .ToList(),
        };
    }

    public static object Binding(UserScriptBinding binding, NexusUser? user = null)
    {
        UserScriptBinding effective = user is null
            ? binding.Clone()
            : UserBindingOverrideResolver.Resolve(user, binding);
        (bool General, bool Notification, bool Advanced) locks = user is null
            ? (false, false, false)
            : UserBindingOverrideResolver.Locks(user);
        return new
        {
            binding.ScriptInstanceId,
            binding.Enabled,
            binding.PreRunScript,
            binding.PreRunOnceOnly,
            binding.PostRunScript,
            binding.PostRunOnFinalOnly,
            binding.NotifyEnabled,
            binding.SmtpTo,
            binding.RunDays,
            binding.Participates,
            effective = new
            {
                effective.Enabled,
                effective.PreRunScript,
                effective.PreRunOnceOnly,
                effective.PostRunScript,
                effective.PostRunOnFinalOnly,
                effective.NotifyEnabled,
                effective.SmtpTo,
                effective.RunDays,
                effective.Participates,
            },
            locks = new
            {
                general = locks.General,
                notification = locks.Notification,
                advanced = locks.Advanced,
            },
        };
    }

    public static object User(
        NexusUser user,
        IReadOnlyList<DispatchQueue> queues)
    {
        (string QueueName, DateTime TriggerTime)? next =
            RuntimeContext.Instance.Scheduler.NextTriggerForUser(user, queues);
        return new
        {
            user.Id,
            user.Index,
            user.Name,
            user.Remark,
            bindingOverrides = (user.BindingOverrides ?? new UserBindingOverrides()).Clone(),
            bindingCount = user.Bindings.Count,
            nextRunAt = next?.TriggerTime,
            nextQueueName = next?.QueueName,
            bindings = user.Bindings.Select(binding => Binding(binding, user)).ToList(),
        };
    }

    public static object Queue(DispatchQueue queue)
    {
        return new
        {
            queue.Id,
            queue.Name,
            queue.Index,
            queue.AutoRunMode,
            queue.CompletionAction,
            queue.TimeSets,
            queue.Tasks,
            queue.NotifyEnabled,
            nextTrigger = RuntimeContext.Instance.Scheduler.NextTriggerFor(queue),
        };
    }

    public static object Settings(AppSettings settings)
    {
        return new
        {
            settings.AutoStart,
            settings.MinimizeToTray,
            settings.LightweightMode,
            settings.AutoOpenBrowser,
            settings.HistoryRetentionDays,
            settings.WebPort,
            settings.McpEnabled,
            settings.McpPort,
            settings.ProxyMode,
            settings.ProxyUrl,
            settings.ProxyUsername,
            proxyPassword = Mask(settings.ProxyPassword),
            settings.WebhookEnabled,
            settings.SmtpEnabled,
            settings.WebhookType,
            webhookUrl = Mask(settings.WebhookUrl),
            webhookSecret = Mask(settings.WebhookSecret),
            settings.WebhookTemplate,
            settings.WebhookTimeout,
            settings.SmtpHost,
            settings.SmtpPort,
            settings.SmtpSecure,
            settings.SmtpUser,
            smtpPassword = Mask(settings.SmtpPassword),
            settings.SmtpFrom,
            settings.SmtpTo,
            settings.SmtpSubjectPrefix,
            settings.SmtpTimeout,
            settings.LogLevel,
            // MCP 始终 loopback；远程 Web 开关和令牌设置不作为 MCP 写入项，但读取时保留脱敏状态。
            settings.AllowRemoteAccess,
            accessToken = Mask(settings.AccessToken),
            settings.UpdateCheckEnabled,
            settings.UpdateChannel,
            settings.UpdateSourceUrl,
        };
    }

    public static object PluginStore(PluginStoreSnapshot snapshot)
    {
        return new
        {
            snapshot.Available,
            snapshot.Stale,
            fetchedAt = snapshot.FetchedAt.ToString("O"),
            snapshot.Error,
            plugins = snapshot.Plugins.Select(plugin => new
            {
                plugin.Name,
                artifactName = plugin.ArtifactName,
                plugin.DisplayName,
                gameName = plugin.GameName,
                plugin.Description,
                plugin.Version,
                kind = plugin.Kind,
                apiVersion = plugin.ApiVersion,
                plugin.Capabilities,
                minHostVersion = plugin.MinHostVersion,
                plugin.Installed,
                plugin.InstalledName,
                plugin.InstalledVersion,
                plugin.UpdateAvailable,
                plugin.Compatible,
                plugin.CompatibilityReason,
                plugin.ManagedByStore,
                plugin.PendingAction,
                plugin.PendingVersion,
                plugin.Status,
                authors = plugin.Authors.Select(author => new { name = author.Name, url = author.Url }).ToList(),
                tags = plugin.Tags,
                homepage = plugin.Homepage,
                updatedAt = plugin.UpdatedAt,
                hasReadme = plugin.HasReadme,
                changelog = plugin.Changelog.Select(change => new
                {
                    change.Version,
                    change.Date,
                    change.Items,
                }).ToList(),
            }).ToList(),
        };
    }

    private static string Mask(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : "enc:***";
    }
}
