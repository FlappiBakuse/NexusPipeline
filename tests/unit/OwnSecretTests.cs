using NexusPipeline.Core.Secrets;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class OwnSecretTests
{
    [Theory(DisplayName = "A Declared Own Extension Scope Authorizes Only The Extension Field Namespace")]
    [InlineData(SecretAccessKind.Status)]
    [InlineData(SecretAccessKind.Read)]
    [InlineData(SecretAccessKind.Write)]
    public void RequiresDeclaredOwner(SecretAccessKind access)
    {
        var policy = new OwnSecretPolicy();
        var field = new SecretFieldKey("host", Guid.NewGuid(), "proxy-password");
        policy.AssertAllowed(field, access, SecretSubjectScopes.OwnExtension);
        policy.AssertAllowed(field, access, SecretSubjectScopes.OwnExtension | SecretSubjectScopes.SelectedBinding);
        Assert.Throws<UnauthorizedAccessException>(() => policy.AssertAllowed(SecretFieldKey.HostProxyPassword(field.SubjectId),
            access, SecretSubjectScopes.OwnExtension));
        Assert.Throws<UnauthorizedAccessException>(() => policy.AssertAllowed(SecretFieldKey.NotificationTask(Guid.NewGuid(),
            field.SubjectId, NotificationSecretField.WebhookUrl), access, SecretSubjectScopes.OwnExtension));
    }

    [Theory(DisplayName = "A Selected Binding Declaration Or Unknown Scope Does Not Mint An Invocation Grant")]
    [InlineData(0, 0)]
    [InlineData(0, 2)]
    [InlineData(0, 4)]
    [InlineData(0, 5)]
    [InlineData(3, 1)]
    public void RejectsUndeclaredScopes(int access, int scopes)
    {
        var policy = new OwnSecretPolicy();
        var field = new SecretFieldKey("fixture-extension", Guid.NewGuid(), "credential");
        Assert.Throws<UnauthorizedAccessException>(() => policy.AssertAllowed(field, (SecretAccessKind)access, (SecretSubjectScopes)scopes));
    }
}
