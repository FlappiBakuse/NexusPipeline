using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Core.Automation;

public enum AutomationTargetKind { Pc, Android }
public enum AutomationPcReadiness { Process, VisibleWindow }
public enum AutomationAndroidDriver { GenericAdb, Mumu }

public sealed record AutomationTarget(AutomationTargetKind Kind, bool LaunchByHost, int WaitSeconds,
    bool ForceCloseAtEnd, AutomationPcTarget? Pc, AutomationAndroidTarget? Android);
public sealed record AutomationPcTarget(AutomationPath LaunchExecutable, ImmutableArray<string> Arguments,
    AutomationPath ReadyExecutable, AutomationPcReadiness ReadinessMode);
public sealed record AutomationAndroidTarget(AutomationAndroidDriver Driver, AutomationPath Tool,
    string Endpoint, string Package, ImmutableArray<string> StartArguments)
{
    public AutomationPath? ScreenshotTool { get; init; }
    public AutomationEmulatorSelection? Emulator { get; init; }
}

public sealed record AutomationEmulatorSelection(string Vendor, Guid RootId, string TargetId, string Generation)
{
    public string? ExtensionId { get; init; }
    public string? ProviderId { get; init; }
}

public static class AutomationTargetPolicy
{
    public static IEnumerable<AutomationPath> Paths(AutomationTarget? target) => target?.Kind switch
    {
        AutomationTargetKind.Pc when target.Pc is { } pc => [pc.LaunchExecutable, pc.ReadyExecutable],
        AutomationTargetKind.Android when target.Android is { } android => android.ScreenshotTool is { } screenshot
            ? [android.Tool, screenshot] : [android.Tool],
        _ => []
    };

    public static void Validate(AutomationTarget? target, IReadOnlySet<Guid> roots)
    {
        if (target is null) return;
        if (!Enum.IsDefined(target.Kind) || target.WaitSeconds is < 0 or > 604800 ||
            (target.Kind == AutomationTargetKind.Pc) != (target.Pc is not null) ||
            (target.Kind == AutomationTargetKind.Android) != (target.Android is not null)) Reject();
        foreach (var path in Paths(target))
            if (path is null || !roots.Contains(path.RootId) || !AutomationCatalogPolicy.RelativePath(path.RelativePath) ||
                path.RelativePath.IndexOfAny(['*', '?', '{', '}']) >= 0) Reject();
        if (target.Pc is { } pc)
        {
            if (!Enum.IsDefined(pc.ReadinessMode) || !Arguments(pc.Arguments) ||
                Extension(pc.LaunchExecutable) is not ("exe" or "cmd" or "bat" or "com" or "ps1") ||
                Extension(pc.ReadyExecutable) != "exe") Reject();
        }
        if (target.Android is { } android)
        {
            if (!Enum.IsDefined(android.Driver) || Extension(android.Tool) != "exe" || !Arguments(android.StartArguments) ||
                !Endpoint(android.Endpoint) || !Package(android.Package) ||
                android.ScreenshotTool is { } screenshot &&
                    (android.Driver != AutomationAndroidDriver.Mumu || Extension(screenshot) != "exe")) Reject();
            if (android.Emulator is { } emulator && (android.Driver != AutomationAndroidDriver.GenericAdb ||
                !Identifier(emulator.Vendor, 80) || emulator.RootId != android.Tool.RootId ||
                (emulator.ExtensionId is null) != (emulator.ProviderId is null) ||
                emulator.ExtensionId is { } owner && !Identifier(owner, 80) || emulator.ProviderId is { } provider && !Identifier(provider, 96) ||
                !roots.Contains(emulator.RootId) || !FileTreePolicy.Digest(emulator.TargetId) || !FileTreePolicy.Digest(emulator.Generation))) Reject();
        }
    }

    public static AutomationTarget? Freeze(AutomationTarget? target) => target is null ? null : target with
    {
        Pc = target.Pc is not { } pc ? null : pc with
        {
            LaunchExecutable = pc.LaunchExecutable with { }, ReadyExecutable = pc.ReadyExecutable with { },
            Arguments = pc.Arguments.Select(value => value).ToImmutableArray()
        },
        Android = target.Android is not { } android ? null : android with
        {
            Tool = android.Tool with { }, StartArguments = android.StartArguments.Select(value => value).ToImmutableArray(),
            ScreenshotTool = android.ScreenshotTool is { } screenshot ? screenshot with { } : null,
            Emulator = android.Emulator is { } emulator ? emulator with { } : null
        }
    };

    private static bool Identifier(string value, int limit) => value.Length > 0 && value.Length <= limit &&
        value[0] is >= 'a' and <= 'z' && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '.');
    private static bool Arguments(ImmutableArray<string> values) => !values.IsDefault && values.Length <= 128 &&
        values.All(value => value is not null && value.Length <= 4096 && !value.Contains('\0'));
    private static string Extension(AutomationPath path) => path.RelativePath[(path.RelativePath.LastIndexOf('.') + 1)..].ToLowerInvariant();
    private static bool Endpoint(string value) => value is { Length: >= 3 and <= 512 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '-' or '[' or ']') &&
        Uri.TryCreate("tcp://" + value, UriKind.Absolute, out var uri) && uri.Port is >= 1 and <= 65535 &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    private static bool Package(string value) => value is { Length: >= 1 and <= 256 } &&
        value.Split('.').All(part => part.Length > 0 && (char.IsAsciiLetter(part[0]) || part[0] == '_') &&
            part.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'));
    private static void Reject() => throw new AutomationDefinitionException("automation_target_invalid");
}
