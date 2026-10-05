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

public sealed class ConfigContractTests
{
    private sealed class BindingAdmission : IUserMutationAdmission
    {
        public void WithCoordination(Action mutation) => mutation();
    }

    [Fact]
    public void BindingCasRejectsEditsAndAcceptsOnlyExactIdempotentRecovery()
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
        Assert.Contains("unsupported_config_format", error);
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
