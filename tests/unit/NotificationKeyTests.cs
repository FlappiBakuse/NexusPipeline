using System.Text.Json;
using NexusPipeline.Core.Secrets;
using Xunit;

namespace NexusPipeline.CoreTests;

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
        Assert.Equal(key, JsonSerializer.Deserialize<SecretFieldKey>(JsonSerializer.Serialize(key)));
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
