using ExecutionTarget = NexusPipeline.Core.Execution.ExecutionTarget;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class StartIdentityTests
{
    private static readonly Guid TargetId = Guid.Parse("a1234567-89ab-4cde-8123-456789abcdef");
    private static readonly Guid OtherId = Guid.Parse("b1234567-89ab-4cde-8123-456789abcdef");
    private static readonly Guid OperationId = Guid.Parse("c1234567-89ab-4cde-8123-456789abcdef");
    private const string Subject = "identity-fixture";

    [Fact(DisplayName = "The Exact Canonical Request Encoding Produces A Deterministic Lowercase Digest")]
    public void HashesCanonicalRequests()
    {
        const string canonical = """{"operationKind":"execution.start","targetKind":"binding","ids":["a1234567-89ab-4cde-8123-456789abcdef"],"expectedCatalogRevision":7,"expectedSettingsRevision":0}""";
        var first = ExecutionStartIdentity.Create(OperationId, Subject, Request());
        var second = ExecutionStartIdentity.Create(OperationId, Subject, Request());
        Assert.Equal(first, second); Assert.Equal(OperationId, first.OperationId); Assert.Equal(Subject, first.SubjectId);
        Assert.Equal("execution.start", first.Kind);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), first.RequestDigest);
        Assert.Matches("^[0-9a-f]{64}$", first.RequestDigest);
    }

    [Theory(DisplayName = "Equivalent Guid Values And Different Current Cultures Keep Every Target Digest Stable")]
    [InlineData("binding")]
    [InlineData("script")]
    [InlineData("script-user")]
    [InlineData("queue")]
    public void IgnoresCultureDifferences(string kind)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var first = ExecutionStartIdentity.Create(OperationId, Subject, Request(kind) with { ExpectedCatalogRevision = 9007199254740991 });
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var canonicalId = Guid.Parse(TargetId.ToString("D").ToUpperInvariant());
            var equivalent = Request(kind, canonicalId) with { ExpectedCatalogRevision = 9007199254740991 };
            Assert.Equal(first, ExecutionStartIdentity.Create(OperationId, Subject, equivalent));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact(DisplayName = "Identical Ids In Different Target Kinds Cannot Collide Through Abstract Target Serialization")]
    public void SeparatesTargetKinds()
    {
        var targets = new ExecutionTarget[] { new BindingTarget(TargetId), new ScriptTarget(TargetId),
            new ScriptUserTarget(TargetId, TargetId), new QueueTarget(TargetId) };
        var digests = targets.Select(target => ExecutionStartIdentity.Create(OperationId, Subject, new(target, 7, 0)).RequestDigest).ToArray();
        Assert.Equal(4, digests.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory(DisplayName = "Every Target Identifier And Expected Revision Participates In The Request Digest")]
    [InlineData("binding-id")]
    [InlineData("script-id")]
    [InlineData("script-user-script-id")]
    [InlineData("script-user-user-id")]
    [InlineData("queue-id")]
    [InlineData("catalog-revision")]
    [InlineData("settings-revision")]
    public void BindsRequestFields(string field)
    {
        var request = Request(field switch { "script-id" => "script", "script-user-script-id" or "script-user-user-id" => "script-user",
            "queue-id" => "queue", _ => "binding" });
        var changed = field switch
        {
            "binding-id" => request with { Target = new BindingTarget(OtherId) },
            "script-id" => request with { Target = new ScriptTarget(OtherId) },
            "script-user-script-id" => request with { Target = new ScriptUserTarget(OtherId, OtherId) },
            "script-user-user-id" => request with { Target = new ScriptUserTarget(TargetId, TargetId) },
            "queue-id" => request with { Target = new QueueTarget(OtherId) },
            "catalog-revision" => request with { ExpectedCatalogRevision = 8 }, _ => request with { ExpectedSettingsRevision = 1 }
        };
        Assert.NotEqual(ExecutionStartIdentity.Create(OperationId, Subject, request).RequestDigest,
            ExecutionStartIdentity.Create(OperationId, Subject, changed).RequestDigest);
    }

    [Theory(DisplayName = "Operation And Subject Stay Separate Identity Fields Without Changing The Request Digest")]
    [InlineData("operation")]
    [InlineData("subject")]
    [InlineData("subject-whitespace")]
    public void SeparatesOperationIdentity(string field)
    {
        var original = ExecutionStartIdentity.Create(OperationId, Subject, Request());
        var changed = ExecutionStartIdentity.Create(field == "operation" ? OtherId : OperationId,
            field == "subject" ? "another-subject" : field == "subject-whitespace" ? Subject + " " : Subject, Request());
        Assert.NotEqual(original, changed); Assert.Equal(original.RequestDigest, changed.RequestDigest);
        Assert.Equal(field == "operation" ? OtherId : OperationId, changed.OperationId); Assert.Equal("execution.start", changed.Kind);
    }

    [Theory(DisplayName = "Invalid Requests Use The Same Core Validation Owner Before Any Identity Can Be Issued")]
    [InlineData("request")]
    [InlineData("target")]
    [InlineData("binding")]
    [InlineData("script")]
    [InlineData("script-user-script")]
    [InlineData("script-user-user")]
    [InlineData("queue")]
    [InlineData("unsupported")]
    public void RejectsInvalidRequests(string kind)
    {
        var request = kind == "request" ? null! : new ExecutionSelectionRequest(kind switch
        {
            "target" => null!, "binding" => new BindingTarget(Guid.Empty), "script" => new ScriptTarget(Guid.Empty),
            "script-user-script" => new ScriptUserTarget(Guid.Empty, OtherId), "script-user-user" => new ScriptUserTarget(TargetId, Guid.Empty),
            "queue" => new QueueTarget(Guid.Empty), _ => new UnsupportedTarget()
        }, 7, 0);
        Assert.Equal("execution_selection_request_invalid", Assert.Throws<ExecutionSelectionException>(() => ExecutionSelectionPolicy.FreezeRequest(request)).Message);
        Assert.Equal("execution_selection_request_invalid", Assert.Throws<ExecutionSelectionException>(() => ExecutionStartIdentity.Create(OperationId, Subject, request)).Message);
    }

    [Theory(DisplayName = "Out Of Range Catalog Or Settings Revisions Cannot Be Hashed As Valid Start Requests")]
    [InlineData(-1L, 0L)]
    [InlineData(9007199254740992L, 0L)]
    [InlineData(7L, -1L)]
    [InlineData(7L, 9007199254740992L)]
    public void RejectsInvalidRevisions(long catalog, long settings)
    {
        Assert.Equal("execution_selection_request_invalid", Assert.Throws<ExecutionSelectionException>(() =>
            ExecutionStartIdentity.Create(OperationId, Subject, Request() with { ExpectedCatalogRevision = catalog, ExpectedSettingsRevision = settings })).Message);
    }

    [Theory(DisplayName = "Invalid Operation Or Subject Metadata Still Obeys Operation Identity Validation")]
    [InlineData("operation")]
    [InlineData("null-subject")]
    [InlineData("empty-subject")]
    [InlineData("long-subject")]
    public void ValidatesOperationIdentity(string field)
    {
        var subject = field switch { "null-subject" => null!, "empty-subject" => " \t", "long-subject" => new string('s', 257), _ => Subject };
        Assert.Throws<ArgumentException>(() => ExecutionStartIdentity.Create(field == "operation" ? Guid.Empty : OperationId, subject, Request()));
    }

    [Theory(DisplayName = "Identity Creation Does Not Read The Exclusively Locked Database Or Write Any Instance State")]
    [InlineData(false)]
    [InlineData(true)]
    public void HashesWithoutState(bool valid)
    {
        using var fixture = new PlanBuilderFixture(); var before = fixture.Snapshot();
        using (new FileStream(fixture.Database.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            if (valid) Assert.Equal("execution.start", ExecutionStartIdentity.Create(OperationId, Subject, Request()).Kind);
            else Assert.Equal("execution_selection_request_invalid", Assert.Throws<ExecutionSelectionException>(() =>
                ExecutionStartIdentity.Create(OperationId, Subject, Request() with { Target = new BindingTarget(Guid.Empty) })).Message);
        }
        var after = fixture.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }

    private static ExecutionSelectionRequest Request(string kind = "binding", Guid? id = null) => new(kind switch
    {
        "script" => new ScriptTarget(id ?? TargetId), "script-user" => new ScriptUserTarget(id ?? TargetId, OtherId),
        "queue" => new QueueTarget(id ?? TargetId), _ => new BindingTarget(id ?? TargetId)
    }, 7, 0);
    private sealed record UnsupportedTarget : ExecutionTarget;
}
