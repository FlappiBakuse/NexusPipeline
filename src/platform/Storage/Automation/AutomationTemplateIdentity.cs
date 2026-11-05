using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Automation;
using AutomationTarget = NexusPipeline.Core.Automation.AutomationTarget;

namespace NexusPipeline.Platform.Storage.Automation;

internal static class AutomationTemplateIdentity
{
    internal static Guid RootId(Guid operationId, Guid sourceId, bool isTemplate) =>
        isTemplate ? Derive(operationId, "root", sourceId) : sourceId;

    internal static Guid AttachmentId(Guid operationId, Guid sourceId, bool isTemplate) =>
        isTemplate ? Derive(operationId, "attachment", sourceId) : sourceId;

    internal static ParsedAutomationBundle Remap(ParsedAutomationBundle source, Guid operationId)
    {
        if (!source.IsScriptTemplate) return source;
        var roots = source.Definitions.Roots.ToDictionary(value => value.Id, value => Derive(operationId, "root", value.Id));
        AutomationPath Map(AutomationPath path) => path with { RootId = roots[path.RootId] };
        return source with
        {
            Definitions = source.Definitions with
            {
                Roots = source.Definitions.Roots.Select(value => value with { Id = roots[value.Id] }).ToImmutableArray(),
                Scripts = source.Definitions.Scripts.Select(value => value with
                {
                    Target = RemapTarget(value.Target, Map),
                    Id = Derive(operationId, "script", value.Id), RootId = roots[value.RootId], Executable = Map(value.Executable),
                    ConfigurationPaths = value.ConfigurationPaths.Select(Map).ToImmutableArray(), LogPaths = value.LogPaths.Select(Map).ToImmutableArray()
                }).ToImmutableArray()
            },
            Attachments = source.Attachments.Select(value => value with { Id = Derive(operationId, "attachment", value.Id) }).ToImmutableArray()
        };
    }

    private static AutomationTarget? RemapTarget(AutomationTarget? target, Func<AutomationPath, AutomationPath> map) => target is null ? null : target with
    {
        Pc = target.Pc is not { } pc ? null : pc with { LaunchExecutable = map(pc.LaunchExecutable), ReadyExecutable = map(pc.ReadyExecutable) },
        Android = target.Android is not { } android ? null : android with { Tool = map(android.Tool) }
    };

    private static Guid Derive(Guid operationId, string kind, Guid sourceId)
    {
        // Stable request-scoped IDs let a preview and its replay create the same independent template instance.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"nexus.automation.template/1|{operationId:D}|{kind}|{sourceId:D}"));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }
}
