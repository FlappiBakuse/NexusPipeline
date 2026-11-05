using NexusPipeline.Core.Configuration;
using System.Collections.Immutable;

namespace NexusPipeline.Core.Automation;

public enum AutomationKind { Generic, Specialized }
public enum AutomationRootKind { Script, External }
public enum AutomationJudgementLanguage { Keywords, JavaScript, Python }
public enum AutomationCompletionAction { None, Shutdown, Hibernate, Restart, Exit }
public enum AutomationDefinitionKind { Root, Script, User, Binding, Queue, Schedule }

public sealed record AutomationPath(Guid RootId, string RelativePath);
public sealed record AutomationAdapterResource(string ResourceId, int DeclarationIndex, string RelativePath, long DefinitionRevision,
    ConfigurationSyntax Syntax, ConfigurationDocumentAccess Access);
public sealed record AutomationLimits(int MaximumAttempts, int StallTimeoutSeconds, int TotalTimeoutSeconds);
public sealed record AutomationJudgement(AutomationJudgementLanguage Language, ImmutableArray<string> SuccessKeywords,
    ImmutableArray<string> FailureKeywords, string? Script);
public sealed record AutomationRoot(Guid Id, long Revision, string Name, AutomationRootKind Kind, string Path);
public sealed record AutomationScript(Guid Id, long Revision, string Name, AutomationKind Kind, Guid RootId,
    string? ExtensionId, AutomationPath Executable, ImmutableArray<string> Arguments,
    ImmutableArray<AutomationPath> ConfigurationPaths, ImmutableArray<AutomationPath> LogPaths,
    AutomationLimits Limits, AutomationJudgement Judgement)
{
    public int Order { get; init; }
    public AutomationTarget? Target { get; init; }
    public ImmutableArray<AutomationAdapterResource> AdapterResources { get; init; } = [];
    public bool UpdateConfigurationAtFinish { get; init; } = true;
}
public sealed record AutomationUser(Guid Id, long Revision, string Name, int Order)
{
    public string Notes { get; init; } = "";
    public string Avatar { get; init; } = "";
    public AutomationBindingOverrides BindingOverrides { get; init; } = new();
}
public sealed record AutomationHook(AutomationPath Executable, ImmutableArray<string> Arguments);
public sealed record AutomationBinding(Guid Id, long Revision, Guid UserId, Guid ScriptId, bool IsEnabled, int Priority)
{
    public int RunDays { get; init; } = -1;
    public int MaximumSuccessfulRunsPerDay { get; init; } = -1;
    public AutomationHook? PreHook { get; init; }
    public AutomationHook? PostHook { get; init; }
    public bool PreOnceAfterSuccess { get; init; }
    public bool PostFinalOnly { get; init; }
    public bool NotifyEnabled { get; init; } = true;
    public ImmutableArray<Guid> NotificationTaskIds { get; init; } = [];
}
public sealed record AutomationQueue(Guid Id, long Revision, string Name, ImmutableArray<Guid> ScriptIds,
    AutomationCompletionAction CompletionAction)
{
    public int Order { get; init; }
    public ImmutableArray<AutomationQueueDependency> SuccessDependencies { get; init; } = [];
    public bool RunOnStartup { get; init; }
    public bool ScheduleEnabled { get; init; } = true;
    public bool NotifyEnabled { get; init; }
    public ImmutableArray<Guid> NotificationTaskIds { get; init; } = [];
}
public sealed record AutomationSchedule(Guid Id, long Revision, Guid QueueId, bool IsEnabled, string TimeZoneId,
    ImmutableArray<int> Weekdays, int LocalMinute)
{
    public int Order { get; init; }
}

public sealed record AutomationCatalog(long Revision, ImmutableArray<AutomationRoot> Roots,
    ImmutableArray<AutomationScript> Scripts, ImmutableArray<AutomationUser> Users,
    ImmutableArray<AutomationBinding> Bindings, ImmutableArray<AutomationQueue> Queues,
    ImmutableArray<AutomationSchedule> Schedules)
{
    public static AutomationCatalog Empty { get; } = new(0, [], [], [], [], [], []);
}

public sealed record RemoveAutomationDefinition(AutomationDefinitionKind Kind, Guid Id, long ExpectedRevision);
public sealed record AutomationChanges(long ExpectedCatalogRevision, ImmutableArray<AutomationRoot> Roots,
    ImmutableArray<AutomationScript> Scripts, ImmutableArray<AutomationUser> Users,
    ImmutableArray<AutomationBinding> Bindings, ImmutableArray<AutomationQueue> Queues,
    ImmutableArray<AutomationSchedule> Schedules, ImmutableArray<RemoveAutomationDefinition> Removed);

public sealed class AutomationConflictException() : Exception("automation_revision_conflict");
public sealed class AutomationDefinitionException(string code) : Exception(code);

