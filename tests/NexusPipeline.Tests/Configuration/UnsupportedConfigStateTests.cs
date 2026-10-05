using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class UnsupportedConfigStateTests : IDisposable
{
    private readonly List<string> _ownedScripts = [];

    public void Dispose()
    {
        foreach (string script in _ownedScripts)
        {
            foreach (string root in new[] { AppPaths.DataDir, Path.GetTempPath(), Path.Combine(AppPaths.InternalDir, "config-resets") })
            {
                string owned = Path.GetFullPath(Path.Combine(root, script));
                Assert.Equal(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(owned));
                if (Directory.Exists(owned)) Directory.Delete(owned, true);
            }
        }
    }

    [Fact]
    public void OldRecoveryInputsBlockRunAndEditingWithoutAdoptingSharedFiles()
    {
        foreach (string kind in new[] { "reset", "archive", "session" })
        {
            string script = "unsupported-" + Guid.NewGuid().ToString("N"), user = "account";
            _ownedScripts.Add(script);
            string site = Path.Combine(Path.GetTempPath(), script, "site");
            Directory.CreateDirectory(site);
            File.WriteAllText(Path.Combine(site, "native.json"), "shared-account");
            string userDir = ConfigPaths.UserDir(script, user);
            Directory.CreateDirectory(userDir);
            string old = kind == "reset"
                ? Path.Combine(AppPaths.InternalDir, "config-resets", script, user + ".json")
                : kind == "archive" ? Path.Combine(userDir, "migration-backups", "original.json")
                : ConfigSessionMark.MarkFile(script, user);
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            File.WriteAllText(old, "{\"Migration\":{},\"SessionPhase\":\"migration-prepared\"}");
            string work = Path.Combine(ConfigPaths.WorkDir(script, user), "script");
            Directory.CreateDirectory(work);
            File.WriteAllText(Path.Combine(work, "evidence"), "retain");
            string beforeUser = ConfigSnapshotFingerprint.Fingerprint(userDir);
            string beforeSite = ConfigSnapshotFingerprint.Fingerprint(site);
            ConfigSwapSession.ConfigureRecovery(_ => null, () => []);
            Assert.False(ConfigExchangeService.PrepareForRun(script, user, site, out string? error));
            Assert.Contains("unsupported_recovery_format", error);
            Assert.NotNull(ConfigEditSessionService.PrepareForEditFresh(script, user, site));
            Assert.NotNull(ConfigEditSessionService.PrepareForEditReuse(script, user, site));
            ConfigWorkDirMaintenance.SweepIdleWorkDir(script, user);
            Assert.Equal(beforeUser, ConfigSnapshotFingerprint.Fingerprint(userDir));
            Assert.Equal(beforeSite, ConfigSnapshotFingerprint.Fingerprint(site));
            Assert.False(ConfigSnapshotService.HasSnapshot(script, user));
        }
    }

    [Fact]
    public void UnsupportedPrimaryCannotBeOverwrittenUsingCurrentBackup()
    {
        string script = "journal-" + Guid.NewGuid().ToString("N"), user = "account";
        _ownedScripts.Add(script);
        var mark = new ConfigSessionMark { ScriptId = script, UserId = user, ConfigPath = Path.GetTempPath(), ConfigKind = "dir", SessionPhase = "run" };
        mark.Write();
        string primary = ConfigSessionMark.MarkFile(script, user);
        JsonObject json = JsonNode.Parse(File.ReadAllText(primary))!.AsObject();
        json["Migration"] = new JsonObject();
        File.WriteAllText(primary, json.ToJsonString());
        string before = ConfigSnapshotFingerprint.Fingerprint(ConfigPaths.UserDir(script, user));
        Assert.Null(ConfigSessionMark.TryRead(script, user));
        Assert.Throws<IOException>(() => mark.Write());
        Assert.Throws<IOException>(() => ConfigSessionMark.Clear(script, user));
        Assert.Equal(before, ConfigSnapshotFingerprint.Fingerprint(ConfigPaths.UserDir(script, user)));
        foreach (string damaged in new[] { "{broken", "null", "[]" })
        {
            File.WriteAllText(primary, damaged);
            before = ConfigSnapshotFingerprint.Fingerprint(ConfigPaths.UserDir(script, user));
            Assert.Null(ConfigSessionMark.TryRead(script, user));
            Assert.Throws<IOException>(() => mark.Write());
            Assert.Throws<IOException>(() => ConfigSessionMark.Clear(script, user));
            Assert.Equal(before, ConfigSnapshotFingerprint.Fingerprint(ConfigPaths.UserDir(script, user)));
        }
    }
}
