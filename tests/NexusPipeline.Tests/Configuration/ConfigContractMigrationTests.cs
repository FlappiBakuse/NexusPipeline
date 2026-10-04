using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Snapshots;
using Xunit;
using NexusPipeline.Host.State;
using NexusPipeline.Host.Composition.Adapters;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users.UseCases;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Tests.Configuration;

public sealed class ConfigContractMigrationTests
{
    private sealed class BindingAdmission : IUserMutationAdmission
    {
        public void WithCoordination(Action mutation) => mutation();
    }

    [Fact]
    public void MigrationBindingCasRejectsEditsAndAcceptsOnlyExactIdempotentRecovery()
    {
        byte[]? previousUsers = File.Exists(AppPaths.UsersPath) ? File.ReadAllBytes(AppPaths.UsersPath) : null;
        try
        {
            var state = new AutomationDefinitionState();
            var binding = new UserScriptBinding { ScriptInstanceId = "migration-cas", RunDays = 8,
                ConfigInputs = new() { ["instance"] = "old", ["unrelated"] = "preserve" } };
            state.Mutate(s => s.Users.Add(new NexusUser { Id = "migration-user", Name = "migration-user", Bindings = [binding] }));
            var commands = new UserCommands(new RuntimeUserMutationState(state), new BindingAdmission(),
                null!, null!, null!, null!, null!, null!, null!);
            string originalHash = UserCommands.BindingFingerprint(binding);
            Assert.True(commands.CommitPendingConfigInput("migration-user", "migration-cas", "instance", "chosen", originalHash, "old").Succeeded);
            byte[] committed = File.ReadAllBytes(AppPaths.UsersPath);
            Assert.True(commands.CommitPendingConfigInput("migration-user", "migration-cas", "instance", "chosen", originalHash, "old").Succeeded);
            Assert.Equal(committed, File.ReadAllBytes(AppPaths.UsersPath));
            state.Mutate(s => s.Users[0].Bindings[0].RunDays = 3);
            Assert.False(commands.CommitPendingConfigInput("migration-user", "migration-cas", "instance", "chosen", originalHash, "old").Succeeded);
            Assert.Equal(committed, File.ReadAllBytes(AppPaths.UsersPath));
            Assert.Equal(3, state.FindUser("migration-user")!.Bindings[0].RunDays);
            Assert.Equal("preserve", state.FindUser("migration-user")!.Bindings[0].ConfigInputs["unrelated"]);
        }
        finally
        {
            if (previousUsers is null) File.Delete(AppPaths.UsersPath);
            else File.WriteAllBytes(AppPaths.UsersPath, previousUsers);
        }
    }

    [Fact]
    public void MigrationStagesRecoverIdempotentlyAndPreserveOtherAccountAndNativeFiles()
    {
        foreach (string fault in new[] { "prepared", "main-committed", "migration-target-committed", "migration-binding-committed" })
        {
            string script = "migrate-" + Guid.NewGuid().ToString("N"), user = "a";
            string root = Path.Combine(Path.GetTempPath(), script), site = Path.Combine(root, "config");
            Directory.CreateDirectory(site);
            byte[] native = "{\"TaskItems\":[{\"entry\":\"Mail\",\"default_check\":null}]}"u8.ToArray();
            byte[] legacy = "{\"instances\":[{\"id\":\"old\"}]}\r\n"u8.ToArray();
            File.WriteAllBytes(Path.Combine(site, "native.json"), native);
            string extra = Path.Combine(root, "appsettings.json");
            File.WriteAllText(extra, "{\"DefaultConfig\":\"Default\"}");
            string store = ConfigPaths.StoreDir(script, user), other = ConfigPaths.StoreDir(script, "b");
            Directory.CreateDirectory(store); Directory.CreateDirectory(other);
            File.WriteAllBytes(Path.Combine(store, "old.json"), legacy);
            File.WriteAllBytes(Path.Combine(other, "untouched.json"), legacy);
            ConfigStoreMetadata.Save(script, user, ConfigStoreMetadata.For(site));
            string originalSite = ConfigMigrationTransaction.Fingerprint(site);
            string originalExtra = ConfigMigrationTransaction.Fingerprint(extra);
            string otherBytes = ConfigMigrationTransaction.Fingerprint(other);
            var mark = new ConfigSessionMark { ScriptId = script, UserId = user, ConfigPath = site,
                ConfigKind = "dir", ConfigContractId = "native.instances.v1", PendingConfigInput = new() { Name = "instance", Value = "chosen" },
                ExtraConfigPaths = ConfigSessionMark.FromExtraPaths([extra]) };
            string archive = ConfigMigrationTransaction.Prepare(mark, () => true);
            int bindings = 0;
            bool Commit(ConfigSessionMark saved) { Assert.Equal("chosen", saved.PendingConfigInput!.Value); bindings++; return true; }
            if (fault != "prepared")
                Assert.Throws<IOException>(() => ConfigMigrationTransaction.Resume(mark, Commit,
                    phase => { if (phase == fault) throw new IOException("injected migration interruption"); }));
            ConfigSwapSession.ConfigureRecovery(_ => null, () => [], Commit);
            ConfigSwapSession.RecoverIfNeeded(script, user, site);
            ConfigSwapSession.RecoverIfNeeded(script, user, site);
            Assert.Equal(1, bindings);
            Assert.Equal(native, File.ReadAllBytes(Path.Combine(store, "native.json")));
            Assert.False(File.Exists(Path.Combine(store, "old.json")));
            Assert.Equal(legacy, File.ReadAllBytes(Path.Combine(archive, "previous-main", "old.json")));
            Assert.Equal(originalSite, ConfigMigrationTransaction.Fingerprint(site));
            Assert.Equal(originalExtra, ConfigMigrationTransaction.Fingerprint(extra));
            Assert.Equal(otherBytes, ConfigMigrationTransaction.Fingerprint(other));
            Assert.Equal("native.instances.v1", ConfigStoreMetadata.Load(script, user)!.ConfigContractId);
            Assert.False(File.Exists(ConfigSessionMark.MarkFile(script, user)));
            Assert.DoesNotContain(ConfigRecoveryService.SnapshotIsolation(), item => item.ScriptId == script || item.ScopeQuality == "unavailable");
        }
        foreach (string phase in new[] { "prepared", "main-committed", "migration-target-committed" })
        {
            string script = "conflict-" + Guid.NewGuid().ToString("N"), user = "a";
            string root = Path.Combine(Path.GetTempPath(), script), site = Path.Combine(root, "native");
            Directory.CreateDirectory(site);
            File.WriteAllText(Path.Combine(site, "task.json"), "{\"new\":true}");
            string store = ConfigPaths.StoreDir(script, user);
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(store, "task.json"), "{\"old\":true}");
            ConfigStoreMetadata.Save(script, user, ConfigStoreMetadata.For(site));
            var mark = new ConfigSessionMark { ScriptId = script, UserId = user, ConfigPath = site, ConfigKind = "dir",
                ConfigContractId = "native.instances.v1", PendingConfigInput = new() { Name = "instance", Value = "a" } };
            string archive = ConfigMigrationTransaction.Prepare(mark, () => true);
            if (phase != "prepared") Assert.Throws<IOException>(() => ConfigMigrationTransaction.Resume(mark, _ => true,
                p => { if (p == phase) throw new IOException("injected interruption"); }));
            File.WriteAllText(Path.Combine(store, "task.json"), "{\"external\":true}");
            int commits = 0;
            ConfigSwapSession.ConfigureRecovery(_ => null, () => [], _ => { commits++; return true; });
            Assert.Contains("migration_conflict", Assert.Throws<IOException>(() => ConfigSwapSession.RecoverIfNeeded(script, user, site)).Message);
            Assert.Equal(0, commits);
            Assert.Equal("{\"external\":true}", File.ReadAllText(Path.Combine(store, "task.json")));
            Assert.Equal("{\"old\":true}", File.ReadAllText(Path.Combine(archive, "previous-main", "task.json")));
            Assert.True(File.Exists(ConfigSessionMark.MarkFile(script, user)));
            string evidence = Path.Combine(root, "conflict-evidence");
            Assert.False(Directory.Exists(evidence));
            Directory.Move(ConfigPaths.UserDir(script, user), evidence);
        }
    }

    [Fact]
    public void SamePathContractChangePreservesOldSnapshotAndSiteBeforeSwap()
    {
        ConfigSwapSession.ConfigureRecovery(_ => null, () => []);
        string script = "contract-" + Guid.NewGuid().ToString("N"), user = "account-a";
        string root = Path.Combine(Path.GetTempPath(), script), site = Path.Combine(root, "config");
        Directory.CreateDirectory(site);
        byte[] native = "{\"TaskItems\":[]}"u8.ToArray(), legacy = "{\"instances\":[]}\r\n"u8.ToArray();
        File.WriteAllBytes(Path.Combine(site, "instance.json"), native);
        string store = ConfigPaths.StoreDir(script, user);
        Directory.CreateDirectory(store);
        File.WriteAllBytes(Path.Combine(store, "old.json"), legacy);
        var old = ConfigStoreMetadata.For(site);
        ConfigStoreMetadata.Save(script, user, old);
        string metadataPath = ConfigPaths.StoreMetadataPath(script, user);
        var oldJson = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
        oldJson.Remove("ConfigContractId");
        File.WriteAllText(metadataPath, oldJson.ToJsonString());
        byte[] metadataBytes = File.ReadAllBytes(metadataPath);
        Assert.NotNull(ConfigStoreMetadata.Load(script, user));

        var runtime = new ConfigSessionRuntimeMetadata(root, "", "", "profile", "fixture", "1", "dir")
        { ConfigContractId = "native.instances.v1", RejectImplicitRebind = true };
        Assert.False(ConfigExchangeService.PrepareForRun(script, user, site, out string? error, runtime));
        Assert.Contains("migration_required", error);
        Assert.Equal(legacy, File.ReadAllBytes(Path.Combine(store, "old.json")));
        Assert.Equal(native, File.ReadAllBytes(Path.Combine(site, "instance.json")));
        Assert.Equal(metadataBytes, File.ReadAllBytes(metadataPath));
        Assert.False(File.Exists(ConfigSessionMark.MarkFile(script, user)));

        Assert.False(ConfigExchangeService.PrepareForRun(script, user, site, out error,
            runtime with { ConfigContractId = "", RequiredConfigRelativePath = "instances/missing.json" }));
        Assert.Contains("config_instance_missing", error);
        Assert.Equal(legacy, File.ReadAllBytes(Path.Combine(store, "old.json")));
        Assert.Equal(native, File.ReadAllBytes(Path.Combine(site, "instance.json")));
        Assert.Equal(metadataBytes, File.ReadAllBytes(metadataPath));

        var mark = new ConfigSessionMark { ScriptId = script, UserId = user, ConfigPath = site,
            ConfigKind = "dir", SessionPhase = "run", ConfigContractId = runtime.ConfigContractId };
        mark.Write();
        Assert.Equal(runtime.ConfigContractId, ConfigSessionMark.TryRead(script, user)!.ConfigContractId);
        var restored = ConfigStoreMetadata.FromMark(mark);
        Assert.Equal(runtime.ConfigContractId, ConfigStoreMetadata.Clone(restored).ConfigContractId);
        Assert.False(old.Matches(restored));
        Assert.True(restored.Matches(ConfigStoreMetadata.Clone(restored)));
        File.Copy(ConfigSessionMark.MarkFile(script, user), Path.Combine(root, "verified-session.json"));
        ConfigSessionMark.Clear(script, user);
    }
}
