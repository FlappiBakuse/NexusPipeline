using System.Collections.Immutable;
using NexusPipeline.Core.Automation;

namespace NexusPipeline.StorageTests;

internal static class AutomationDefinitionSeed
{
    internal static AutomationChanges Create(string root)
    {
        var source = Path.Combine(root, "script");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "run.exe"), [1, 2, 3]);
        var selected = new AutomationRoot(Guid.NewGuid(), 0, "Script root", AutomationRootKind.Script, source);
        var script = new AutomationScript(Guid.NewGuid(), 0, "Script", AutomationKind.Generic, selected.Id, null, new(selected.Id, "run.exe"),
            ["--fixture"], [], [], new(2, 30, 120), new(AutomationJudgementLanguage.Keywords, ["done"], [], null));
        var users = new[] { new AutomationUser(Guid.NewGuid(), 0, "First", 0), new AutomationUser(Guid.NewGuid(), 0, "Second", 1) }.ToImmutableArray();
        var bindings = users.Select((user, index) => new AutomationBinding(Guid.NewGuid(), 0, user.Id, script.Id, false, index)).ToImmutableArray();
        var queue = new AutomationQueue(Guid.NewGuid(), 0, "Ordered", [script.Id], AutomationCompletionAction.None);
        var schedule = new AutomationSchedule(Guid.NewGuid(), 0, queue.Id, false, "UTC", [1, 3, 5], 720);
        return new(0, [selected], [script], users, bindings, [queue], [schedule], []);
    }
}
