using NexusPipeline.Core.Automation;
using NexusPipeline.Extensibility.Protocol;
using WireTarget = NexusPipeline.Platform.Serialization.Protocol.ExecutionTarget;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionTargetInputs
{
    internal static WireTarget? Capture(NexusPipeline.Core.Automation.AutomationTarget? target, IReadOnlyDictionary<Guid, AutomationRoot> roots,
        ImmutableBlobStore blobs, Guid instanceId, PreparedMuMuDiscovery? discovery = null,
        NexusPipeline.Platform.Serialization.Protocol.EmulatorTargetFrozen? emulator = null)
    {
        if (target is null) return null;
        AutomationTargetPolicy.Validate(target, roots.Keys.ToHashSet());
        string Resolve(AutomationPath path) => PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(roots[path.RootId].Path, path.RelativePath));
        if (target.Pc is { } pc)
        {
            var directory = Path.GetDirectoryName(Resolve(pc.LaunchExecutable))!;
            var identity = PhysicalObjectIdentity.Read(directory);
            var launch = ExecutionLaunchInputs.Capture(pc.LaunchExecutable, pc.Arguments, directory,
                new() { VolumeSerial = identity.VolumeSerial, FileId = identity.FileId }, roots);
            return new()
            {
                Kind = "pc", LaunchByHost = target.LaunchByHost, WaitSeconds = target.WaitSeconds, ForceCloseAtEnd = target.ForceCloseAtEnd,
                Pc = new()
                {
                    Launch = new()
                    {
                        LaunchKind = launch.LaunchKind, Source = launch.Source, Command = launch.Command,
                        SourceArguments = launch.SourceArguments, Arguments = launch.Arguments,
                        WorkingDirectory = launch.WorkingDirectory, WorkingDirectoryIdentity = launch.WorkingDirectoryIdentity
                    },
                    ReadyImage = ExecutionHookImages.Capture(Resolve(pc.ReadyExecutable), runtime: false),
                    ReadinessMode = pc.ReadinessMode == AutomationPcReadiness.Process ? "process" : "visibleWindow"
                }
            };
        }
        var android = target.Android ?? throw Invalid();
        if ((android.Emulator is null) != (emulator is null)) throw Invalid();
        var endpoint = ExecutionAndroidCommands.Endpoint(android.Endpoint);
        _ = ExecutionAndroidCommands.Generic(endpoint, target.LaunchByHost ? ExecutionAndroidAction.StartApplication :
            ExecutionAndroidAction.DeviceState, android.StartArguments, android.Package);
        var manager = ExecutionHookImages.Capture(Resolve(android.Tool), runtime: true);
        ExecutionTargetAndroidMapping? mapping = null;
        if (android.Driver == AutomationAndroidDriver.Mumu)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            if (discovery is null) throw new IOException("execution_mumu_discovery_required");
            mapping = discovery.Use(endpoint, manager, proof =>
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                var saved = MuMuDiscoveryEvidence.RequireMapping(blobs, proof.InfoEvidence, endpoint, manager, proof.Index);
                if (saved.InstanceId != instanceId) throw new IOException("execution_mumu_instance_unbound");
                return new ExecutionTargetAndroidMapping
                {
                    Index = proof.Index, EndpointPort = new Uri("http://" + endpoint).Port,
                    InfoEvidence = new() { RelativePath = proof.InfoEvidence.RelativePath,
                        SizeBytes = proof.InfoEvidence.SizeBytes, Sha256 = proof.InfoEvidence.Sha256 },
                    ManagerImageSha256 = proof.ManagerImageSha256
                };
            });
        }
        return new()
        {
            Kind = "android", LaunchByHost = target.LaunchByHost, WaitSeconds = target.WaitSeconds, ForceCloseAtEnd = target.ForceCloseAtEnd,
            Android = new()
            {
                Driver = android.Driver == AutomationAndroidDriver.Mumu ? "mumu" : "genericAdb", Tool = manager,
                Endpoint = endpoint, Package = android.Package, StartArguments = android.StartArguments.ToArray(), Mapping = mapping, Emulator = emulator,
                ScreenshotTool = android.ScreenshotTool is { } screenshot
                    ? ExecutionHookImages.Capture(Resolve(screenshot), runtime: true) : null
            }
        };
    }

    internal static void Require(WireTarget? target, NexusPipeline.Platform.Serialization.Protocol.AutomationTarget? definition,
        IReadOnlyDictionary<string, ExecutionPlanRootBindingsItem> roots)
    {
        if (definition is null)
        {
            if (target is not null) throw Invalid();
            return;
        }
        if (target is null || target.Kind != definition.Kind || target.LaunchByHost != definition.LaunchByHost ||
            target.WaitSeconds != definition.WaitSeconds || target.ForceCloseAtEnd != definition.ForceCloseAtEnd) throw Invalid();
        if (definition.Pc is { } pc)
        {
            if (target.Pc is not { } frozen || target.Android is not null || frozen.ReadinessMode != pc.ReadinessMode) throw Invalid();
            var source = ExecutionLaunchInputs.Resolve(pc.LaunchExecutable, roots);
            ExecutionLaunchInputs.Require(new(frozen.Launch.LaunchKind, frozen.Launch.Source, frozen.Launch.Command,
                frozen.Launch.SourceArguments, frozen.Launch.Arguments, frozen.Launch.WorkingDirectory, frozen.Launch.WorkingDirectoryIdentity),
                source, pc.Arguments, Path.GetDirectoryName(source)!, frozen.Launch.Source.ParentIdentity);
            if (frozen.ReadyImage.Path != ExecutionLaunchInputs.Resolve(pc.ReadyExecutable, roots) || frozen.ReadyImage.RuntimeFiles.Count != 0) throw Invalid();
            ExecutionLaunchInputs.RequireImage(frozen.ReadyImage);
        }
        else if (definition.Android is { } android)
        {
            if (target.Android is not { } frozen || target.Pc is not null || frozen.Driver != android.Driver ||
                frozen.Tool.Path != ExecutionLaunchInputs.Resolve(android.Tool, roots) ||
                frozen.Endpoint != ExecutionAndroidCommands.Endpoint(android.Endpoint) || frozen.Package != android.Package ||
                !frozen.StartArguments.SequenceEqual(android.StartArguments)) throw Invalid();
            ExecutionLaunchInputs.RequireImage(frozen.Tool);
            if ((frozen.Emulator is null) != (android.Emulator is null) || frozen.Emulator is { } emulator && emulator.Selection != android.Emulator) throw Invalid();
            if ((frozen.ScreenshotTool is null) != (android.ScreenshotTool is null)) throw Invalid();
            if (frozen.ScreenshotTool is { } screenshot)
            {
                if (frozen.Driver != "mumu" || android.ScreenshotTool is null ||
                    screenshot.Path != ExecutionLaunchInputs.Resolve(android.ScreenshotTool, roots)) throw Invalid();
                ExecutionLaunchInputs.RequireImage(screenshot);
            }
            _ = ExecutionAndroidCommands.Generic(frozen.Endpoint, target.LaunchByHost ? ExecutionAndroidAction.StartApplication :
                ExecutionAndroidAction.DeviceState, frozen.StartArguments, frozen.Package);
            if (frozen.Driver == "genericAdb" && frozen.Mapping is not null || frozen.Driver == "mumu" &&
                (frozen.Mapping is null || frozen.Mapping.ManagerImageSha256 != frozen.Tool.Sha256 ||
                    frozen.Mapping.EndpointPort != new Uri("http://" + frozen.Endpoint).Port ||
                    !new Uri("http://" + frozen.Endpoint).IsLoopback)) throw Invalid();
        }
        else throw Invalid();
    }

    internal static IEnumerable<ExecutionPlanInterpreterImage> Images(ExecutionPlanStep step)
    {
        if (step.Target?.Pc is { } pc) return [pc.Launch.Source, pc.Launch.Command, pc.ReadyImage];
        if (step.Target?.Android is { } android) return new[] { android.Tool, android.ScreenshotTool, android.Emulator?.Control }.OfType<ExecutionPlanInterpreterImage>();
        return [];
    }

    internal static IEnumerable<(string Path, long Size, string Sha256, FileTreePhysicalIdentity Identity, FileTreePhysicalIdentity Parent)> Files(ExecutionPlanStep step)
    {
        foreach (var image in Images(step))
        {
            yield return (image.Path, image.SizeBytes, image.Sha256, image.Identity, image.ParentIdentity);
            foreach (var file in image.RuntimeFiles)
                yield return (Path.GetFullPath(Path.Combine(Path.GetDirectoryName(image.Path)!, file.RelativePath)),
                    file.SizeBytes, file.Sha256, file.Identity, file.ParentIdentity);
        }
    }

    internal static IReadOnlyList<BlobReference> MappingReferences(ExecutionPlan plan, ImmutableBlobStore blobs, bool verifyContent)
    {
        var result = new List<BlobReference>();
        foreach (var android in plan.Steps.Select(step => step.Target?.Android).OfType<ExecutionTargetAndroid>())
        {
            if (android.Emulator is { } emulator)
                result.AddRange(NexusPipeline.Platform.Storage.Emulation.EmulatorTargetProof.Require(emulator, android, blobs, verifyContent));
            if (android.Driver == "genericAdb")
            {
                if (android.Mapping is not null) throw Invalid();
                continue;
            }
            if (android.Driver != "mumu") throw Invalid();
            var mapping = android.Mapping ?? throw Invalid();
            var reference = new BlobReference(mapping.InfoEvidence.RelativePath, mapping.InfoEvidence.SizeBytes, mapping.InfoEvidence.Sha256);
            var saved = MuMuDiscoveryEvidence.Read(blobs, reference);
            if (saved.InstanceId.ToString("D") != plan.InstanceId || saved.Endpoint != android.Endpoint ||
                !MuMuDiscoveryEvidence.SameImage(saved.Manager, android.Tool) || !saved.StartReturned || !saved.OutputComplete ||
                saved.ExitCode != 0 || mapping.Index < 0 || saved.MappingIndex != mapping.Index || mapping.ManagerImageSha256 != android.Tool.Sha256 ||
                mapping.EndpointPort != new Uri("http://" + android.Endpoint).Port) throw Invalid();
            if (verifyContent)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                _ = MuMuDiscoveryEvidence.RequireMapping(blobs, reference, android.Endpoint, android.Tool, mapping.Index);
            }
            result.AddRange(MuMuDiscoveryEvidence.ReadReferences(blobs, reference));
        }
        return result.Distinct().ToArray();
    }

    private static IOException Invalid() => new("execution_target_source_unbound");
}
