using System.Text.Json;
using NexusPipeline.Core.Secrets;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class HostKeyTests
{
    [Fact(DisplayName = "A Host Proxy Field Binds Its Instance And Host Owner Without An Extension Identity")]
    public void BindsHostOwner()
    {
        var instance = Guid.NewGuid();
        var key = SecretFieldKey.HostProxyPassword(instance);
        Assert.Equal(FieldOwnerKind.Host, key.OwnerKind);
        Assert.Null(key.ExtensionId);
        Assert.Equal(instance, key.SubjectId);
        Assert.Equal("proxy-password", key.Field);
    }

    [Fact(DisplayName = "An Extension Named Host Cannot Alias The Fixed Host Proxy Field Or Authorize It")]
    public void RejectsExtensionAliases()
    {
        var subject = Guid.NewGuid();
        var host = SecretFieldKey.HostProxyPassword(subject);
        var extension = new SecretFieldKey("host", subject, "proxy-password");
        Assert.NotEqual(host, extension);
        Assert.Equal(FieldOwnerKind.Extension, extension.OwnerKind);
        extension.RequireExtensionOwner("host");
        Assert.Throws<UnauthorizedAccessException>(() => host.RequireExtensionOwner("host"));
        Assert.Throws<UnauthorizedAccessException>(() => extension.RequireExtensionOwner("another"));
    }

    [Theory(DisplayName = "Unknown Owner Kinds Cannot Become An Extension Or Host Field")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void RejectsUnknownOwners(int owner)
    {
        Assert.Throws<ArgumentException>(() => new SecretFieldKey((FieldOwnerKind)owner, null, Guid.NewGuid(), "proxy-password"));
    }

    [Theory(DisplayName = "Host Does Not Accept An Arbitrary Field Or Any Extension Identity")]
    [InlineData("arbitrary", null)]
    [InlineData("proxy-password", "host")]
    [InlineData("proxy-password", "fixture-extension")]
    [InlineData("proxy-password", "")]
    public void RejectsForeignFields(string field, string? extension)
    {
        Assert.Throws<ArgumentException>(() => new SecretFieldKey(FieldOwnerKind.Host, extension, Guid.NewGuid(), field));
    }

    [Fact(DisplayName = "A Host Field Requires The Actual Nonempty Instance Identity")]
    public void RequiresInstanceIdentity()
    {
        Assert.Throws<ArgumentException>(() => SecretFieldKey.HostProxyPassword(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new SecretFieldKey("", Guid.NewGuid(), "proxy-password"));
    }

    [Theory(DisplayName = "Missing Or Unknown Json Owner Does Not Default To Extension")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsMissingOwners(bool unknown)
    {
        var owner = unknown ? "\"OwnerKind\":87," : "";
        var json = "{" + owner + "\"ExtensionId\":\"fixture-extension\",\"SubjectId\":\"" +
            Guid.NewGuid().ToString("D") + "\",\"Field\":\"credential\"}";
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<SecretFieldKey>(json));
    }
}

public sealed class NotificationKeyTests
{
    [Theory(DisplayName = "A Notification Field Has An Exact Instance Task And Fixed Field Identity")]
    [InlineData(NotificationSecretField.WebhookUrl, "webhook-url")]
    [InlineData(NotificationSecretField.WebhookSecret, "webhook-secret")]
    [InlineData(NotificationSecretField.FeishuAppSecret, "feishu-app-secret")]
    [InlineData(NotificationSecretField.SlackBotToken, "slack-bot-token")]
    [InlineData(NotificationSecretField.SmtpPassword, "smtp-password")]
    public void BindsNotificationIdentity(NotificationSecretField field, string name)
    {
        var instance = Guid.NewGuid(); var task = Guid.NewGuid();
        var key = SecretFieldKey.NotificationTask(instance, task, field);
        Assert.Equal(FieldOwnerKind.NotificationTask, key.OwnerKind);
        Assert.Equal(instance, key.OwnerInstanceId); Assert.Equal(task, key.SubjectId);
        Assert.Null(key.ExtensionId); Assert.Equal(name, key.Field);
        Assert.Equal(field, NotificationSecretFields.Parse(name));
    }

    [Fact(DisplayName = "An Extension Or Another Instance Cannot Alias Or Authorize A Notification Task Field")]
    public void RejectsForeignOwners()
    {
        var instance = Guid.NewGuid(); var task = Guid.NewGuid();
        var key = SecretFieldKey.NotificationTask(instance, task, NotificationSecretField.WebhookUrl);
        Assert.NotEqual(key, new SecretFieldKey("notification", task, key.Field));
        Assert.NotEqual(key, SecretFieldKey.NotificationTask(Guid.NewGuid(), task, NotificationSecretField.WebhookUrl));
        Assert.Throws<UnauthorizedAccessException>(() => key.RequireExtensionOwner("notification"));
        Assert.Throws<UnauthorizedAccessException>(key.RequireGenericRead);
        Assert.Throws<UnauthorizedAccessException>(key.RequireStandaloneMutation);
    }

    [Theory(DisplayName = "Arbitrary Or Incomplete Notification Secret Keys Are Rejected")]
    [InlineData("field")]
    [InlineData("instance")]
    [InlineData("task")]
    [InlineData("extension")]
    [InlineData("owner")]
    public void RejectsIncompleteKeys(string fault)
    {
        var instance = Guid.NewGuid(); var task = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new SecretFieldKey(
            fault == "owner" ? FieldOwnerKind.Unknown : FieldOwnerKind.NotificationTask,
            fault == "extension" ? "fixture-extension" : null, fault == "task" ? Guid.Empty : task,
            fault == "field" ? "arbitrary-password" : "webhook-url", fault == "instance" ? null : instance));
        Assert.Throws<ArgumentException>(() => NotificationSecretFields.Name((NotificationSecretField)(-1)));
    }

    [Theory(DisplayName = "Existing Secret Owners Cannot Borrow A Notification Instance Scope")]
    [InlineData(FieldOwnerKind.Host)]
    [InlineData(FieldOwnerKind.Extension)]
    public void RejectsBorrowedScope(FieldOwnerKind owner)
    {
        Assert.Throws<ArgumentException>(() => new SecretFieldKey(owner,
            owner == FieldOwnerKind.Extension ? "fixture-extension" : null,
            Guid.NewGuid(), owner == FieldOwnerKind.Host ? "proxy-password" : "credential", Guid.NewGuid()));
    }
}

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
