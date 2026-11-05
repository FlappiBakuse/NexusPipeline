using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Configuration;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Automation;

public sealed record AutomationImportAttachment(Guid Id, string PackagePath, long SizeBytes, string Sha256);
public sealed record ParsedAutomationBundle(Guid BundleId, bool IsScriptTemplate, string InputDigest,
    AutomationChanges Definitions, ImmutableArray<AutomationImportAttachment> Attachments);

public sealed class AutomationBundleCodec(ContractReader contracts)
{
    public ParsedAutomationBundle Decode(ReadOnlyMemory<byte> input, IReadOnlyDictionary<Guid, string> selectedRoots, long expectedRevision)
    {
        if (input.Length is 0 or > StrictJson.MaximumBytes) throw new JsonException("invalid_json_size");
        var ownedInput = input.ToArray();
        var wire = contracts.Read<AutomationBundle>(ownedInput, "automation-bundle.schema.json");
        var bundleId = Id(wire.BundleId);
        var roots = wire.Roots.Select(value => new AutomationRoot(Id(value.Id), 0, value.Name,
            Enum.Parse<AutomationRootKind>(value.Kind, true), selectedRoots.TryGetValue(Id(value.Id), out var path)
                ? path : throw new AutomationDefinitionException("automation_import_root_required"))).ToImmutableArray();
        if (selectedRoots.Count != roots.Length || selectedRoots.Keys.Any(key => roots.All(value => value.Id != key)))
            throw new AutomationDefinitionException("automation_import_roots_unbound");
        var scripts = wire.Scripts.Select(Script).ToImmutableArray();
        var changes = new AutomationChanges(expectedRevision, roots, scripts,
            wire.Users.Select(User).ToImmutableArray(),
            wire.Bindings.Select(Binding).ToImmutableArray(),
            wire.Queues.Select(Queue).ToImmutableArray(),
            wire.Schedules.Select(Schedule).ToImmutableArray(), []);
        _ = AutomationEdits.Apply(AutomationCatalog.Empty, changes with { ExpectedCatalogRevision = 0 });
        var template = wire.ExportKind == "scriptTemplate";
        if (template && (scripts.Length != 1 || scripts[0].Kind != AutomationKind.Generic))
            throw new AutomationDefinitionException("automation_template_requires_generic_script");
        var attachments = wire.Attachments.Select(value => new AutomationImportAttachment(Id(value.Id), value.Path, value.SizeBytes, value.Sha256)).ToImmutableArray();
        if (attachments.Select(value => value.Id).Distinct().Count() != attachments.Length ||
            attachments.Select(value => value.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != attachments.Length)
            throw new AutomationDefinitionException("automation_attachment_identity_duplicated");
        return new(bundleId, template, Convert.ToHexStringLower(SHA256.HashData(ownedInput)), changes, attachments);
    }

    public byte[] Encode(AutomationCatalog catalog, DateTimeOffset createdAt, Guid? templateScriptId = null,
        IReadOnlyList<AutomationImportAttachment>? selectedAttachments = null)
    {
        AutomationCatalogPolicy.Validate(catalog);
        var scripts = templateScriptId is { } selected
            ? catalog.Scripts.Where(value => value.Id == selected).ToImmutableArray() : catalog.Scripts;
        if (templateScriptId.HasValue && (scripts.Length != 1 || scripts[0].Kind != AutomationKind.Generic))
            throw new AutomationDefinitionException("automation_template_requires_generic_script");
        var neededRoots = scripts.SelectMany(value => value.ConfigurationPaths.Concat(value.LogPaths).Append(value.Executable).Concat(AutomationTargetPolicy.Paths(value.Target)))
            .Select(value => value.RootId).Concat(scripts.Select(value => value.RootId)).ToHashSet();
        var roots = templateScriptId.HasValue ? catalog.Roots.Where(value => neededRoots.Contains(value.Id)) : catalog.Roots;
        var wire = new AutomationBundle
        {
            Format = "nexus.automation.bundle", Version = 1, BundleId = Guid.NewGuid().ToString("D"),
            CreatedAt = createdAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"), ExportKind = templateScriptId.HasValue ? "scriptTemplate" : "definitions",
            Roots = roots.Select(value => new AutomationPathsRoot { Id = value.Id.ToString("D"), Name = value.Name, Kind = Lower(value.Kind) }).ToArray(),
            Scripts = scripts.Select(Script).ToArray(),
            Users = templateScriptId.HasValue ? [] : catalog.Users.Select(value => new AutomationRelationsUser
                { Id = value.Id.ToString("D"), Name = value.Name, Order = value.Order, Notes = value.Notes, Avatar = value.Avatar, BindingOverrides = BindingPolicyCodec.Write(value.BindingOverrides) }).ToArray(),
            Bindings = templateScriptId.HasValue ? [] : catalog.Bindings.Select(value => new AutomationRelationsBinding
            {
                Id = value.Id.ToString("D"), UserId = value.UserId.ToString("D"), ScriptId = value.ScriptId.ToString("D"), Enabled = value.IsEnabled, Priority = value.Priority,
                RunDays = value.RunDays, MaximumSuccessfulRunsPerDay = value.MaximumSuccessfulRunsPerDay,
                PreHook = Hook(value.PreHook), PostHook = Hook(value.PostHook),
                PreOnceAfterSuccess = value.PreOnceAfterSuccess, PostFinalOnly = value.PostFinalOnly,
                NotifyEnabled = value.NotifyEnabled, NotificationTaskIds = value.NotificationTaskIds.Select(id => id.ToString("D")).ToArray()
            }).ToArray(),
            Queues = templateScriptId.HasValue ? [] : catalog.Queues.Select(value => new AutomationRelationsQueue
            {
                SuccessDependencies = value.SuccessDependencies.Select(item => new AutomationBindingPolicyDependency
                { ScriptId = item.ScriptId.ToString("D"), RequiredScriptIds = item.RequiredScriptIds.Select(id => id.ToString("D")).ToArray() }).ToArray(),
                Id = value.Id.ToString("D"), Name = value.Name, Order = value.Order, ScriptIds = value.ScriptIds.Select(id => id.ToString("D")).ToArray(), CompletionAction = Lower(value.CompletionAction), RunOnStartup = value.RunOnStartup, ScheduleEnabled = value.ScheduleEnabled,
                NotifyEnabled = value.NotifyEnabled, NotificationTaskIds = value.NotificationTaskIds.Select(id => id.ToString("D")).ToArray()
            }).ToArray(),
            Schedules = templateScriptId.HasValue ? [] : catalog.Schedules.Select(value => new AutomationRelationsSchedule
            {
                Id = value.Id.ToString("D"), Order = value.Order, QueueId = value.QueueId.ToString("D"), Enabled = value.IsEnabled, TimeZoneId = value.TimeZoneId,
                Weekdays = value.Weekdays.Select(day => (long)day).ToArray(), LocalMinute = value.LocalMinute, MissedPolicy = "skip"
            }).ToArray(),
            Attachments = (selectedAttachments ?? []).Select(value => new AutomationPathsAttachment
                { Id = value.Id.ToString("D"), Path = value.PackagePath, SizeBytes = value.SizeBytes, Sha256 = value.Sha256 }).ToArray()
        };
        var bytes = BoundedJson.Serialize(wire);
        using var verified = contracts.Read(bytes, "automation-bundle.schema.json");
        return bytes;
    }

    internal static AutomationScript Script(AutomationScriptScript wire) => new(Id(wire.Id), 0, wire.Name,
        Enum.Parse<AutomationKind>(wire.Kind, true), Id(wire.RootId), wire.ExtensionId, Path(wire.Executable), wire.Arguments.ToImmutableArray(),
        wire.ConfigurationPaths.Select(Path).ToImmutableArray(), wire.LogPaths.Select(Path).ToImmutableArray(),
        new(checked((int)wire.Limits.MaximumAttempts), checked((int)wire.Limits.StallTimeoutSeconds), checked((int)wire.Limits.TotalTimeoutSeconds)),
        new(Enum.Parse<AutomationJudgementLanguage>(wire.Judgement.Language, true), wire.Judgement.SuccessKeywords.ToImmutableArray(),
            wire.Judgement.FailureKeywords.ToImmutableArray(), wire.Judgement.Script)) { Order = checked((int)(wire.Order ?? 0)), UpdateConfigurationAtFinish = wire.UpdateConfigurationAtFinish, Target = AutomationTargetCodec.FromWire(wire.Target),
            AdapterResources = wire.AdapterResources.Select(resource => new AutomationAdapterResource(resource.ResourceId, checked((int)resource.DeclarationIndex),
                resource.RelativePath, resource.DefinitionRevision, Enum.Parse<ConfigurationSyntax>(resource.Syntax, true),
                ConfigurationSelectorProtocol.Read(resource.Access))).ToImmutableArray() };

    internal static AutomationScriptScript Script(AutomationScript value) => new()
    {
        Id = value.Id.ToString("D"), Order = value.Order, Name = value.Name, Kind = Lower(value.Kind), RootId = value.RootId.ToString("D"), ExtensionId = value.ExtensionId,
        Target = AutomationTargetCodec.ToWire(value.Target),
        AdapterResources = value.AdapterResources.Select(resource => new AutomationScriptAdapterResource
        { ResourceId = resource.ResourceId, DeclarationIndex = resource.DeclarationIndex, RelativePath = resource.RelativePath, DefinitionRevision = resource.DefinitionRevision, Syntax = resource.Syntax switch
            { ConfigurationSyntax.JsonWithComments => "jsonWithComments", _ => resource.Syntax.ToString().ToLowerInvariant() },
            Access = ConfigurationSelectorProtocol.Write(resource.Access) }).ToArray(),
        UpdateConfigurationAtFinish = value.UpdateConfigurationAtFinish, Executable = Path(value.Executable), Arguments = value.Arguments, ConfigurationPaths = value.ConfigurationPaths.Select(Path).ToArray(),
        LogPaths = value.LogPaths.Select(Path).ToArray(), Limits = new() { MaximumAttempts = value.Limits.MaximumAttempts,
            StallTimeoutSeconds = value.Limits.StallTimeoutSeconds, TotalTimeoutSeconds = value.Limits.TotalTimeoutSeconds },
        Judgement = new() { Language = Lower(value.Judgement.Language), SuccessKeywords = value.Judgement.SuccessKeywords,
            FailureKeywords = value.Judgement.FailureKeywords, Script = value.Judgement.Script }
    };

    internal static AutomationUser User(AutomationRelationsUser value) => new(Id(value.Id), 0, value.Name, checked((int)value.Order))
        { Notes = value.Notes ?? "", Avatar = UserAvatar.Require(value.Avatar ?? ""), BindingOverrides = BindingPolicyCodec.Read(value.BindingOverrides) };
    internal static AutomationBinding Binding(AutomationRelationsBinding value) => new(Id(value.Id), 0, Id(value.UserId), Id(value.ScriptId), value.Enabled, checked((int)value.Priority))
    {
        RunDays = checked((int)(value.RunDays ?? -1)), MaximumSuccessfulRunsPerDay = checked((int)(value.MaximumSuccessfulRunsPerDay ?? -1)),
        PreHook = Hook(value.PreHook), PostHook = Hook(value.PostHook),
        PreOnceAfterSuccess = value.PreOnceAfterSuccess ?? false, PostFinalOnly = value.PostFinalOnly ?? false,
        NotifyEnabled = value.NotifyEnabled, NotificationTaskIds = value.NotificationTaskIds.Select(Id).ToImmutableArray()
    };
    internal static AutomationQueue Queue(AutomationRelationsQueue value) => new(Id(value.Id), 0, value.Name,
        value.ScriptIds.Select(Id).ToImmutableArray(), Enum.Parse<AutomationCompletionAction>(value.CompletionAction, true))
    {
        Order = checked((int)value.Order),
        SuccessDependencies = (value.SuccessDependencies ?? []).Select(item => new AutomationQueueDependency(Id(item.ScriptId),
            item.RequiredScriptIds.Select(Id).ToImmutableArray())).ToImmutableArray(),
        RunOnStartup = value.RunOnStartup ?? false, ScheduleEnabled = value.ScheduleEnabled ?? true, NotifyEnabled = value.NotifyEnabled,
        NotificationTaskIds = value.NotificationTaskIds.Select(Id).ToImmutableArray()
    };
    internal static AutomationSchedule Schedule(AutomationRelationsSchedule value) => new(Id(value.Id), 0, Id(value.QueueId),
        value.Enabled, value.TimeZoneId, value.Weekdays.Select(day => checked((int)day)).ToImmutableArray(), checked((int)value.LocalMinute))
        { Order = checked((int)(value.Order ?? 0)) };

    private static AutomationHook? Hook(AutomationRelationsHook? hook) => hook is null ? null : new(Path(hook.Executable), hook.Arguments.ToImmutableArray());
    private static AutomationRelationsHook? Hook(AutomationHook? hook) => hook is null ? null : new() { Executable = Path(hook.Executable), Arguments = hook.Arguments };

    private static AutomationPath Path(AutomationPathsPortablePath value) => new(Id(value.RootId), value.Relative);
    private static AutomationPathsPortablePath Path(AutomationPath value) => new() { RootId = value.RootId.ToString("D"), Relative = value.RelativePath };
    private static string Lower<T>(T value) where T : Enum => value.ToString().ToLowerInvariant();
    private static Guid Id(string text)
    {
        if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty || value.ToString("D") != text)
            throw new AutomationDefinitionException("automation_import_identity_invalid");
        return value;
    }
}
