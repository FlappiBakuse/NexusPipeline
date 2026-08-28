namespace NexusPipeline.Models;

/// <summary>用户级全局绑定覆盖。每个类别只有一个同步开关，关闭后绑定中的原始值重新生效。</summary>
public sealed class UserBindingOverrides
{
    public UserGeneralOverride General { get; set; } = new();

    public UserNotificationOverride Notification { get; set; } = new();

    public UserAdvancedOverride Advanced { get; set; } = new();

    public UserBindingOverrides Clone()
    {
        return new UserBindingOverrides
        {
            General = General?.Clone() ?? new UserGeneralOverride(),
            Notification = Notification?.Clone() ?? new UserNotificationOverride(),
            Advanced = Advanced?.Clone() ?? new UserAdvancedOverride(),
        };
    }
}

public sealed class UserGeneralOverride
{
    public bool SyncEnabled { get; set; }

    public bool Enabled { get; set; } = true;

    public int RunDays { get; set; } = -1;

    public UserGeneralOverride Clone() => new()
    {
        SyncEnabled = SyncEnabled,
        Enabled = Enabled,
        RunDays = RunDays,
    };
}

public sealed class UserNotificationOverride
{
    public bool SyncEnabled { get; set; }

    public bool NotifyEnabled { get; set; } = true;

    public string SmtpTo { get; set; } = "";

    public UserNotificationOverride Clone() => new()
    {
        SyncEnabled = SyncEnabled,
        NotifyEnabled = NotifyEnabled,
        SmtpTo = SmtpTo,
    };
}

public sealed class UserAdvancedOverride
{
    public bool SyncEnabled { get; set; }

    public string PreRunScript { get; set; } = "";

    public bool PreRunOnceOnly { get; set; }

    public string PostRunScript { get; set; } = "";

    public bool PostRunOnFinalOnly { get; set; }

    public UserAdvancedOverride Clone() => new()
    {
        SyncEnabled = SyncEnabled,
        PreRunScript = PreRunScript,
        PreRunOnceOnly = PreRunOnceOnly,
        PostRunScript = PostRunScript,
        PostRunOnFinalOnly = PostRunOnFinalOnly,
    };
}
