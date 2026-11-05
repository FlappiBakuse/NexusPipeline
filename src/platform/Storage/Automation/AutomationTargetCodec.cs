using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NexusPipeline.Core.Automation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using Target = NexusPipeline.Core.Automation.AutomationTarget;
using WireTarget = NexusPipeline.Platform.Serialization.Protocol.AutomationTarget;

namespace NexusPipeline.Platform.Storage.Automation;

internal static class AutomationTargetCodec
{
    private static readonly JsonSerializerOptions PersistedOptions = PersistenceOptions();
    internal static Target? FromWire(WireTarget? value) => value is null ? null : new(
        value.Kind switch { "pc" => AutomationTargetKind.Pc, "android" => AutomationTargetKind.Android, _ => throw Invalid() },
        value.LaunchByHost, checked((int)value.WaitSeconds), value.ForceCloseAtEnd,
        value.Pc is not { } pc ? null : new(Path(pc.LaunchExecutable), pc.Arguments.ToImmutableArray(), Path(pc.ReadyExecutable),
            pc.ReadinessMode switch { "process" => AutomationPcReadiness.Process, "visibleWindow" => AutomationPcReadiness.VisibleWindow, _ => throw Invalid() }),
        value.Android is not { } android ? null : new(
            android.Driver switch { "genericAdb" => AutomationAndroidDriver.GenericAdb, "mumu" => AutomationAndroidDriver.Mumu, _ => throw Invalid() },
            Path(android.Tool), android.Endpoint, android.Package, android.StartArguments.ToImmutableArray())
        {
            ScreenshotTool = android.ScreenshotTool is { } screenshot ? Path(screenshot) : null,
            Emulator = android.Emulator is not { } emulator ? null : new(emulator.Vendor,
                Guid.ParseExact(emulator.RootId, "D"), emulator.TargetId, emulator.Generation)
        });

    internal static WireTarget? ToWire(Target? value) => value is null ? null : new()
    {
        Kind = value.Kind switch { AutomationTargetKind.Pc => "pc", AutomationTargetKind.Android => "android", _ => throw Invalid() },
        LaunchByHost = value.LaunchByHost, WaitSeconds = value.WaitSeconds, ForceCloseAtEnd = value.ForceCloseAtEnd,
        Pc = value.Pc is not { } pc ? null : new()
        {
            LaunchExecutable = Path(pc.LaunchExecutable), Arguments = pc.Arguments, ReadyExecutable = Path(pc.ReadyExecutable),
            ReadinessMode = pc.ReadinessMode switch { AutomationPcReadiness.Process => "process", AutomationPcReadiness.VisibleWindow => "visibleWindow", _ => throw Invalid() }
        },
        Android = value.Android is not { } android ? null : new()
        {
            Driver = android.Driver switch { AutomationAndroidDriver.GenericAdb => "genericAdb", AutomationAndroidDriver.Mumu => "mumu", _ => throw Invalid() },
            Tool = Path(android.Tool), Endpoint = android.Endpoint, Package = android.Package, StartArguments = android.StartArguments,
            ScreenshotTool = android.ScreenshotTool is { } screenshot ? Path(screenshot) : null,
            Emulator = android.Emulator is not { } emulator ? null : new()
            { Vendor = emulator.Vendor, RootId = emulator.RootId.ToString("D"), TargetId = emulator.TargetId, Generation = emulator.Generation }
        }
    };

    internal static string? Encode(Target? value) => value is null ? null : JsonSerializer.Serialize(value, PersistedOptions);
    internal static Target? Decode(string? value)
    {
        if (value is null) return null;
        using var parsed = StrictJson.Parse(Encoding.UTF8.GetBytes(value));
        var result = parsed.Deserialize<Target>(PersistedOptions) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(result, PersistedOptions))) throw Invalid();
        return result;
    }

    private static JsonSerializerOptions PersistenceOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type != typeof(AutomationAndroidTarget)) return;
            // Absent optional inputs must retain the original canonical target bytes.
            foreach (var name in new[] { nameof(AutomationAndroidTarget.ScreenshotTool), nameof(AutomationAndroidTarget.Emulator) })
                type.Properties.Single(property => property.Name == name).ShouldSerialize = static (_, value) => value is not null;
        });
        return new() { TypeInfoResolver = resolver };
    }

    private static AutomationPath Path(AutomationPathsPortablePath value) => new(Guid.ParseExact(value.RootId, "D"), value.Relative);
    private static AutomationPathsPortablePath Path(AutomationPath value) => new() { RootId = value.RootId.ToString("D"), Relative = value.RelativePath };
    private static IOException Invalid() => new("automation_target_persisted_invalid");
}
