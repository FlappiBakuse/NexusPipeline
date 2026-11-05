using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Settings;

namespace NexusPipeline.CoreTests;

internal sealed class ExecutionSelectionFixture
{
    internal Guid ScriptId { get; } = Guid.Parse("10000000-0000-0000-0000-000000000001");
    internal Guid OtherScriptId { get; } = Guid.Parse("10000000-0000-0000-0000-000000000002");
    internal Guid UserA { get; } = Guid.Parse("20000000-0000-0000-0000-000000000003");
    internal Guid UserB { get; } = Guid.Parse("20000000-0000-0000-0000-000000000001");
    internal Guid UserC { get; } = Guid.Parse("20000000-0000-0000-0000-000000000002");
    internal Guid BindingA { get; } = Guid.Parse("30000000-0000-0000-0000-000000000001");
    internal Guid BindingB { get; } = Guid.Parse("30000000-0000-0000-0000-000000000002");
    internal Guid BindingC { get; } = Guid.Parse("30000000-0000-0000-0000-000000000003");
    internal Guid OtherBinding { get; } = Guid.Parse("30000000-0000-0000-0000-000000000004");
    internal Guid QueueId { get; } = Guid.Parse("40000000-0000-0000-0000-000000000001");
    internal AutomationCatalog Catalog { get; }
    internal HostSettingsSnapshot Settings { get; } = new(0, HostSettings.Default);

    internal ExecutionSelectionFixture()
    {
        var root = new AutomationRoot(Guid.Parse("50000000-0000-0000-0000-000000000001"), 3, "Script", AutomationRootKind.Script, @"D:\CoreFixture\scripts");
        var external = new AutomationRoot(Guid.Parse("50000000-0000-0000-0000-000000000002"), 4, "Config", AutomationRootKind.External, @"D:\CoreFixture\config");
        var script = new AutomationScript(ScriptId, 5, "First", AutomationKind.Generic, root.Id, null, new(root.Id, "first.exe"), ["--frozen"],
            [new(external.Id, "config.json")], [new(root.Id, "log.txt")], new(3, 30, 120), new(AutomationJudgementLanguage.Keywords, ["done"], ["failed"], null));
        var other = script with { Id = OtherScriptId, Revision = 6, Name = "Second", Executable = new(root.Id, "second.exe") };
        Catalog = new(8, [root, external], [script, other], [new(UserA, 2, "Same name", 2), new(UserB, 3, "Same name", 1), new(UserC, 4, "Third", 2)],
            [new(BindingA, 1, UserA, ScriptId, true, 0), new(BindingB, 2, UserB, ScriptId, true, 9), new(BindingC, 3, UserC, ScriptId, true, 1),
                new(OtherBinding, 4, UserB, OtherScriptId, true, 0)],
            [new(QueueId, 9, "Explicit", [ScriptId, OtherScriptId], AutomationCompletionAction.Hibernate)], []);
    }

    internal ExecutionSelection Select(ExecutionTarget target, AutomationCatalog? catalog = null, HostSettingsSnapshot? settings = null)
    {
        catalog ??= Catalog; settings ??= Settings;
        return ExecutionSelectionPolicy.Select(catalog, settings, new(target, catalog.Revision, settings.Revision));
    }

    internal AutomationCatalog Disabled() => Catalog with { Bindings = Catalog.Bindings.Select(binding => binding with { IsEnabled = false }).ToImmutableArray() };

    internal AutomationCatalog Specialized() => Catalog with
    {
        Scripts = Catalog.Scripts.Select(script => script.Id == ScriptId
            ? script with { Kind = AutomationKind.Specialized, ExtensionId = "fixture-extension" } : script).ToImmutableArray()
    };

    internal static ImmutableArray<ExecutionItemReadiness> Ready(ExecutionSelection selection) => selection.Items
        .Where(item => item.Disposition == ExecutionSelectionDisposition.Candidate)
        .Select(item => new ExecutionItemReadiness(item, true, ExecutionSelectionPolicy.InputsVerified)).ToImmutableArray();
}
