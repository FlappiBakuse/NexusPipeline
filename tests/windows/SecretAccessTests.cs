using System.Security.AccessControl;
using System.Security.Principal;
using NexusPipeline.Core.Secrets;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class SecretAccessTests
{
    [Fact(DisplayName = "A Prepared Secret Candidate Retains Its Verified Replay Key Until Publication Ownership Is Released")]
    public void HoldsReplayKey()
    {
        using var fixture = SecretTestRuntime.Create();
        using var value = new SecretValue("held-replay-key-fixture");
        var keyPath = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/keys")));
        var original = File.ReadAllBytes(keyPath);
        using (var draft = new NexusPipeline.Platform.Storage.Secrets.PreparationPlanCodec(fixture.Instance, new(), fixture.Database)
            .Freeze(NexusPipeline.Platform.Storage.Sqlite.SecretFieldStore.Identity(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set),
                Guid.NewGuid(), fixture.Key, 0, value))
        {
            Assert.Throws<IOException>(() => File.WriteAllBytes(keyPath, [18, 0]));
            Assert.Throws<IOException>(() => File.Delete(keyPath));
            Assert.Equal(original, File.ReadAllBytes(keyPath));
        }
        using var writable = new FileStream(keyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(original.Length, writable.Length);
    }

    [Fact(DisplayName = "Fresh Secret Directories And Ciphertext Only Grant The Current User And System Through The Private Acl")]
    public async Task RestrictsPayloadAcls()
    {
        using var fixture = SecretTestRuntime.Create();
        using var value = new SecretValue("private-acl-fixture");
        await fixture.Service.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, value, default);
        using var identity = WindowsIdentity.GetCurrent();
        var expected = new[] { identity.User!.Value, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value }.ToHashSet(StringComparer.Ordinal);
        var root = Path.Combine(fixture.Instance.RuntimeRoot, "secrets");
        var security = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        Verify(security.GetAccessRules(true, true, typeof(SecurityIdentifier)));
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            Verify(new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetAccessRules(true, true, typeof(SecurityIdentifier)));
        using var plaintext = await fixture.Service.ReadAsync(fixture.Key, default);
        plaintext.Use(chars => Assert.True(chars.SequenceEqual("private-acl-fixture")));

        void Verify(AuthorizationRuleCollection rules)
        {
            var allowed = rules.Cast<FileSystemAccessRule>().Where(rule => rule.AccessControlType == AccessControlType.Allow).ToArray();
            Assert.True(allowed.Select(rule => rule.IdentityReference.Value).ToHashSet(StringComparer.Ordinal).SetEquals(expected));
            Assert.All(allowed, rule => Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights));
        }
    }
}
