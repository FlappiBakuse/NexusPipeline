using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class AdmissionPublicationFixture : IDisposable
{
    internal ExecutionAdmissionFixture Admission { get; } = new();
    internal ConfigurationEditStore Store { get; }
    internal ConfigurationEditRequest Request { get; }
    internal ConfigurationEditStatus Edit { get; }
    private readonly PreparedConfigurationEdit _prepared;
    private readonly ResourceOperationLease _root;
    internal string JournalDirectory => Path.Combine(Admission.Source.Instance.RuntimeRoot, "journals", Edit.Resources[0].JournalId.ToString("D"));
    internal PayloadApplyRoots Roots => new(Admission.Source.Root, Path.Combine(Admission.Source.Instance.RuntimeRoot, "extensions"));

    internal AdmissionPublicationFixture(Action<PlanBuilderFixture>? beforeEdit = null)
    {
        var source = Admission.Source;
        try
        {
            beforeEdit?.Invoke(source);
            var catalog = source.Definitions.Read(); var binding = catalog.Bindings.Single(item => item.Id == source.BindingB);
            var script = catalog.Scripts.Single(item => item.Id == source.ScriptId);
            var address = new ConfigurationAddress(binding.Id, GenericConfigurationPolicy.ResourceId(script.ConfigurationPaths.Single()));
            var saved = new ConfigurationRevisionStore(source.Instance, source.Database, new()).Read(address)!;
            Request = new(binding.Revision, [new(address, saved.Revision, saved.Contract, source.Configuration)
                { WritePolicy = new([""], [saved.Tree.Shape]) }])
            {
                Editor = new(source.Image, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source.Image))), [], Path.GetDirectoryName(source.Image)!)
            };
            Store = new(source.Instance, source.Database, new()); var sessionId = Guid.NewGuid();
            _prepared = Store.Prepare(sessionId, Request);
            var claims = PhysicalResourceAdmission.Merge(_prepared.AllPhysicalClaims.Concat([
                new("automation:definitions", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference),
                new($"configuration:script:{script.Id:D}", ResourceClaimMode.Mutation),
                new(ConfigurationRevisionService.ResourceKey(address), ResourceClaimMode.Mutation)]));
            _root = Assert.IsType<ResourceOperationLease>(Admission.Resources.TryAcquire(sessionId, claims).Lease);
            Edit = Store.Begin(ConfigurationEditStore.BeginIdentity(sessionId, "admission-edit", Request), _prepared, PlanBuilderFixture.CreatedAt);
            ConfigurationRecoveryHolds.Publish(source.Database, Admission.Index);
            Assert.False(Directory.Exists(JournalDirectory));
        }
        catch { _prepared?.Dispose(); _root?.Dispose(); Admission.Dispose(); throw; }
    }

    internal AdmissionPublicationEvidence Evidence() => new(Admission.Source.Instance, Admission.Source.Database, new(), Roots);

    internal void DamageInitialPlan(string fault)
    {
        _prepared.Dispose(); var resource = Assert.Single(Edit.Resources);
        var path = Path.Combine(Admission.Source.Instance.RuntimeRoot, resource.PlanPath);
        if (fault == "missing-plan") File.Delete(path);
        else if (fault == "unknown-plan") File.WriteAllText(path, "{\"format\":\"unrecognized.configuration\",\"version\":1}");
        else if (fault == "foreign-plan")
        {
            using var foreign = Store.Prepare(Guid.NewGuid(), Request);
            var other = foreign.Use((_, resources) => Assert.Single(resources));
            Admission.Execute($"UPDATE main.operation_journals SET plan_path='{other.PlanPath}',plan_digest='{other.PlanDigest}' WHERE journal_id='{resource.JournalId:D}';");
        }
        else if (fault == "claim-mode") Admission.Execute($"UPDATE main.journal_resource_claims SET mode=1 WHERE journal_id='{resource.JournalId:D}' AND resource_key='automation:definitions';");
        else throw new ArgumentException("unknown fixture fault", nameof(fault));
    }

    public void Dispose()
    {
        try { _prepared.Dispose(); }
        finally { try { _root.Dispose(); } finally { Admission.Dispose(); } }
    }
}
