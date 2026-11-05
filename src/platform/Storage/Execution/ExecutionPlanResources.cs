using System.Collections.Immutable;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Storage.Execution;

public static class ExecutionPlanResources
{
    public static IReadOnlyList<ExecutionPlanPhysicalClaimsItem> Capture(ExecutionPlan plan)
    {
        var paths = new Dictionary<string, ResourceClaimMode>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, ResourceClaimMode mode)
        {
            if (!paths.ContainsKey(path) || mode == ResourceClaimMode.Mutation) paths[path] = mode;
            if (paths.Count > 4096) throw new IOException("execution_resource_claim_limit");
        }
        foreach (var path in ExecutionCaptureInputs.Paths(plan))
        {
            Add(path, ResourceClaimMode.Reference); Add(Path.GetDirectoryName(path)!, ResourceClaimMode.Reference);
        }
        foreach (var root in plan.RootBindings) Add(root.Path, ResourceClaimMode.Reference);
        foreach (var step in plan.Steps)
        {
            Add(step.ExecutablePath, step.ExclusiveProgram == true ? ResourceClaimMode.Mutation : ResourceClaimMode.Reference);
            if (step.Provider is { } provider) Add(provider.PackageRoot, ResourceClaimMode.Reference);
            foreach (var image in ExecutionProviderInputs.Images(step))
            {
                Add(image.Path, ResourceClaimMode.Reference); Add(Path.GetDirectoryName(image.Path)!, ResourceClaimMode.Reference);
                foreach (var file in image.RuntimeFiles)
                    Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(image.Path)!, file.RelativePath)), ResourceClaimMode.Reference);
            }
            Add(step.WorkingDirectory, step.JudgementWorker is null && step.Hooks is null ? ResourceClaimMode.Reference : ResourceClaimMode.Mutation);
            foreach (var file in ExecutionJudgementImages.Files(step).Concat(ExecutionHookImages.Files(step)).Concat(ExecutionTargetInputs.Files(step)).Concat(ExecutionAdapterBinding.SourceFiles(step)))
            {
                Add(file.Path, ResourceClaimMode.Reference); Add(Path.GetDirectoryName(file.Path)!, ResourceClaimMode.Reference);
            }
            if (step.Target?.Pc is { } pc) Add(pc.Launch.WorkingDirectory, ResourceClaimMode.Reference);
            if (step.Target?.Android?.Emulator is { } emulator) Add(emulator.InstanceDirectory, ResourceClaimMode.Reference);
            foreach (var target in step.ConfigurationTargets.Concat(step.LogTargets.Select(log => log.ScopePath))) Add(target, ResourceClaimMode.Mutation);
        }
        using var directories = OperatingSystem.IsWindows()
            ? DirectoryAttributeScope.HoldDirectories(paths.Keys.Select(path => Path.GetDirectoryName(path)!)) : null;
        var packageRoots = plan.Steps.Where(step => step.Provider is not null).Select(step => step.Provider!.PackageRoot)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claims = new SortedDictionary<string, ResourceClaimMode>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            foreach (var claim in PhysicalResourceAdmission.Read(path.Key, path.Value, includeDescendants: path.Value == ResourceClaimMode.Mutation || packageRoots.Contains(path.Key)))
            {
                if (!claims.ContainsKey(claim.ResourceKey) || claim.Mode == ResourceClaimMode.Mutation) claims[claim.ResourceKey] = claim.Mode;
                if (claims.Count > 4096) throw new IOException("execution_resource_claim_limit");
            }
        }
        foreach (var step in plan.Steps) ProviderLauncherReferences.RequireCanRetain(step, claims);
        return claims.Select(claim => new ExecutionPlanPhysicalClaimsItem
            { ResourceKey = claim.Key, Mode = claim.Value == ResourceClaimMode.Reference ? "reference" : "mutation" }).ToArray();
    }

    public static IReadOnlyList<string> Derive(ExecutionPlan plan) => Claims(plan).Select(claim => claim.ResourceKey).ToArray();

    public static ImmutableArray<ResourceClaim> Claims(ExecutionPlan plan)
    {
        var claims = new List<ResourceClaim>
        {
            new("automation:definitions", ResourceClaimMode.Reference), new("settings:host", ResourceClaimMode.Reference),
            new("blobs:content", ResourceClaimMode.Reference)
        };
        foreach (var step in plan.Steps)
        {
            claims.Add(new("automation:" + step.ScriptId, ResourceClaimMode.Mutation));
            if (step.JudgementWorker is not null) claims.Add(new("secrets:fields", ResourceClaimMode.Reference));
            if (step.Target?.Android is { } android) claims.AddRange(TargetResourceKeys.Android(android.Endpoint, ResourceClaimMode.Mutation));
            if (step.Target?.Pc is { } pc)
            {
                claims.Add(TargetResourceKeys.Pc(pc.ReadyImage));
                if (step.Provider is not null) claims.Add(new("target:pc:desktop", ResourceClaimMode.Mutation));
            }
            if (step.UserId is not null) claims.Add(new("user:" + step.UserId, ResourceClaimMode.Reference));
            if (step.ExtensionId is not null) claims.Add(new("extension:" + step.ExtensionId, ResourceClaimMode.Reference));
            if (step.Provider is { } provider) claims.Add(new("extension:" + provider.ExtensionId, ResourceClaimMode.Reference));
            if (step.Target?.Android?.Emulator is { } emulator) claims.Add(new("extension:" + emulator.ExtensionId, ResourceClaimMode.Reference));
            if (step.Adapter?.CompiledProvider is { } compiler) claims.Add(new("extension:" + compiler, ResourceClaimMode.Reference));
            foreach (var resource in step.Adapter?.CompiledResources ?? [])
            {
                if (!Guid.TryParse(step.BindingId, out var binding)) throw new IOException("execution_compilation_binding_required");
                claims.Add(new(ConfigurationRevisionService.ResourceKey(new(binding, resource)), ResourceClaimMode.Reference));
            }
            if (step.ConfigurationTargets.Count != 0) claims.Add(new("configuration:script:" + step.ScriptId, ResourceClaimMode.Mutation));
        }
        PlanConfigurationSlots.Validate(plan);
        foreach (var slot in plan.ConfigurationSlots)
            claims.Add(new(ConfigurationRevisionService.ResourceKey(PlanConfigurationSlots.Address(slot)), ResourceClaimMode.Mutation));
        var prior = "";
        foreach (var claim in plan.PhysicalClaims)
        {
            if (claim is null || claim.ResourceKey.Length > 512 ||
                !(claim.ResourceKey.StartsWith("filesystem:entry:", StringComparison.Ordinal) ||
                    claim.ResourceKey.StartsWith("filesystem:object:", StringComparison.Ordinal)) ||
                claim.ResourceKey.Any(char.IsControl) || StringComparer.Ordinal.Compare(prior, claim.ResourceKey) >= 0 ||
                claim.Mode is not ("reference" or "mutation")) throw new IOException("execution_physical_claims_invalid");
            prior = claim.ResourceKey;
            claims.Add(new(claim.ResourceKey, claim.Mode == "reference" ? ResourceClaimMode.Reference : ResourceClaimMode.Mutation));
        }
        if (plan.PhysicalClaims.Count is < 1 or > 4096) throw new IOException("execution_physical_claims_invalid");
        var result = PhysicalResourceAdmission.Merge(claims);
        if (result.Length > 4096) throw new IOException("execution_resource_claim_limit");
        return result;
    }

    internal static void RequireCurrent(ExecutionPlan plan)
    {
        if (!Capture(plan).SequenceEqual(plan.PhysicalClaims)) throw new IOException("execution_physical_claims_changed");
    }
}
