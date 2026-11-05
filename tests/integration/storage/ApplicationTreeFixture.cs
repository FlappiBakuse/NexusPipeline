using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ApplicationTreeFixture
{
    internal string Root { get; }
    internal string Target { get; }
    internal string Source { get; }
    internal FileTreeArchive Archive { get; }
    internal FrozenPayloadApply Plan { get; }
    internal ApplicationTreeEffects Effects { get; }
    internal PayloadTreeScene Saved { get; private set; }
    internal string Work => PayloadWorkspacePath.For(Plan);

    internal ApplicationTreeFixture(bool missingReadme = false)
    {
        Root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "application-" + Guid.NewGuid().ToString("N"));
        Target = Path.Combine(Root, "installed"); Source = Path.Combine(Root, "source");
        WritePayload(Target, "original"); WritePayload(Source, "candidate");
        if (missingReadme) File.Delete(Path.Combine(Target, "README.md"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(Target, "runtime"));
        File.WriteAllText(Path.Combine(Target, "runtime", "user.txt"), "runtime-user");
        Directory.CreateDirectory(Path.Combine(Target, "plugins", "user-plugin"));
        File.WriteAllText(Path.Combine(Target, "plugins", "user-plugin", "state.txt"), "plugin-user");
        File.WriteAllText(Path.Combine(Target, "notes.txt"), "unrelated-user");
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        Archive = new(scanner, new(instance, contracts));
        Plan = new(instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), JournalKind.UpdateApply, PayloadAction.Upgrade,
            null, Target, PhysicalObjectIdentity.Read(Root), Archive.CaptureApplication(Target, default),
            Archive.CaptureApplication(Source, default), ["maintenance:host-payload"]);
        Effects = new(scanner, Archive);
        Saved = new(Plan.OperationId, Plan.JournalId, "initial", null, []);
    }

    internal static void WritePayload(string root, string marker)
    {
        Directory.CreateDirectory(Path.Combine(root, "resources", "desktop", "resources"));
        File.WriteAllText(Path.Combine(root, "NexusPipeline.exe"), marker + "-host");
        File.WriteAllText(Path.Combine(root, "NexusPipeline.update.exe"), marker + "-maintenance");
        File.WriteAllText(Path.Combine(root, "README.md"), marker + "-readme");
        File.WriteAllText(Path.Combine(root, "resources", "desktop", "resources", "app.asar"), marker + "-desktop");
        File.WriteAllText(Path.Combine(root, "resources", "payload-manifest.json"), "{\"marker\":\"" + marker + "\"}");
    }

    internal void Materialize()
    {
        Saved = Saved with { Stage = "workspace-created", WorkspaceIdentity = Effects.CreateWorkspace(Plan) };
        Saved = Saved with { Stage = "candidate-materialized", CandidateObjects = Effects.MaterializeCandidate(Plan, Saved, default) };
    }

    internal void Preserve()
    {
        Saved = Saved with { Stage = "original-preservation-started" };
        Effects.PreserveOriginal(Plan, Saved);
        Saved = Saved with { Stage = "original-preserved" };
    }

    internal void Install()
    {
        Effects.InstallCandidate(Plan, Saved);
        Saved = Saved with { Stage = "candidate-installed" };
    }

    internal void PartialPreserve(int count)
    {
        Saved = Saved with { Stage = "original-preservation-started" };
        foreach (var name in ApplicationTree.Roots.Take(count))
        {
            var tree = ApplicationTree.Slice(Plan.Before, name);
            PhysicalObjectMove.MoveVerified(new(Path.Combine(Target, name), Plan.Before.OriginalIdentity!, tree.OriginalIdentity!,
                Path.Combine(Work, "before-" + name), Saved.WorkspaceIdentity!, tree.Snapshot.Shape));
        }
    }

    internal void PartialInstall(int count)
    {
        foreach (var name in ApplicationTree.Roots.Take(count))
        {
            var tree = ApplicationTree.Slice(Plan.Candidate, name, Saved.CandidateObjects);
            PhysicalObjectMove.MoveVerified(new(Path.Combine(Work, "candidate", name),
                Saved.CandidateObjects.Single(item => item.RelativePath.Length == 0).Identity, tree.OriginalIdentity!,
                Path.Combine(Target, name), Plan.Before.OriginalIdentity!, tree.Snapshot.Shape));
        }
    }
}
