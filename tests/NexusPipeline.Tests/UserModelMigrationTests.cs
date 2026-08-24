using System.Text.Json;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

public class UserModelMigrationTests
{
    [Fact]
    public void Migrate_MergesSameNameAcrossScripts_AndMovesDataByUserId()
    {
        string root = MakeTempDir();
        try
        {
            string scriptsPath = Path.Combine(root, "config", "scripts.json");
            string usersPath = Path.Combine(root, "config", "users.json");
            string journalPath = Path.Combine(root, "config", "migrations", "v096-users.json");
            string dataDir = Path.Combine(root, "data");
            string scriptA = "script-a";
            string scriptB = "script-b";
            var scripts = new List<ScriptInstance>
            {
                new()
                {
                    Id = scriptA,
                    Index = 0,
                    Name = "A",
                    Users = new List<ScriptUser>
                    {
                        new() { Name = "Alice", Enabled = true, PreRunScript = "a.cmd" },
                    },
                },
                new()
                {
                    Id = scriptB,
                    Index = 1,
                    Name = "B",
                    Users = new List<ScriptUser>
                    {
                        new() { Name = "alice", Enabled = false, PostRunScript = "b.cmd", PostRunOnFinalOnly = true },
                    },
                },
            };
            Directory.CreateDirectory(Path.GetDirectoryName(scriptsPath)!);
            File.WriteAllText(scriptsPath, JsonSerializer.Serialize(scripts, JsonOpts.Indented));
            Directory.CreateDirectory(Path.Combine(dataDir, scriptA, "Alice", "store"));
            File.WriteAllText(Path.Combine(dataDir, scriptA, "Alice", "store", "state.json"), "A");
            Directory.CreateDirectory(Path.Combine(dataDir, scriptB, "alice", "original"));
            File.WriteAllText(Path.Combine(dataDir, scriptB, "alice", "original", "state.json"), "B");

            UserModelMigration.Migrate(scriptsPath, usersPath, dataDir, journalPath, new DateTime(2026, 8, 24, 12, 0, 0));

            List<NexusUser> users = JsonSerializer.Deserialize<List<NexusUser>>(File.ReadAllText(usersPath), JsonOpts.Default)!;
            NexusUser user = Assert.Single(users);
            Assert.Equal("Alice", user.Name);
            Assert.Equal(2, user.Bindings.Count);
            Assert.Contains(user.Bindings, binding => binding.ScriptInstanceId == scriptA && binding.Enabled && binding.PreRunScript == "a.cmd");
            Assert.Contains(user.Bindings, binding => binding.ScriptInstanceId == scriptB && !binding.Enabled && binding.PostRunScript == "b.cmd" && binding.PostRunOnFinalOnly);
            Assert.All(user.Bindings, binding => Assert.True(binding.NotifyEnabled));

            Assert.True(Directory.Exists(Path.Combine(dataDir, scriptA, user.Id, "store")));
            Assert.True(Directory.Exists(Path.Combine(dataDir, scriptB, user.Id, "original")));
            Assert.False(Directory.Exists(Path.Combine(dataDir, scriptA, "Alice")));
            Assert.False(Directory.Exists(Path.Combine(dataDir, scriptB, "alice")));

            List<ScriptInstance> rewritten = JsonSerializer.Deserialize<List<ScriptInstance>>(File.ReadAllText(scriptsPath), JsonOpts.Default)!;
            Assert.All(rewritten, script => Assert.Empty(script.Users));
            Assert.True(File.Exists(scriptsPath + ".pre-v096-20260824120000000"));
            Assert.Contains("\"Phase\": \"Completed\"", File.ReadAllText(journalPath));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void Migrate_IsIdempotentAfterCompletion()
    {
        string root = MakeTempDir();
        try
        {
            string scriptsPath = Path.Combine(root, "config", "scripts.json");
            string usersPath = Path.Combine(root, "config", "users.json");
            string journalPath = Path.Combine(root, "config", "migrations", "v096-users.json");
            string dataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(Path.GetDirectoryName(scriptsPath)!);
            File.WriteAllText(scriptsPath, "[{\"Id\":\"s\",\"Index\":0,\"Name\":\"S\",\"Users\":[{\"Name\":\"User\"}]}]");

            UserModelMigration.Migrate(scriptsPath, usersPath, dataDir, journalPath, DateTime.Now);
            string usersBefore = File.ReadAllText(usersPath);
            string journalBefore = File.ReadAllText(journalPath);
            UserModelMigration.Migrate(scriptsPath, usersPath, dataDir, journalPath, DateTime.Now.AddMinutes(1));

            Assert.Equal(usersBefore, File.ReadAllText(usersPath));
            Assert.Equal(journalBefore, File.ReadAllText(journalPath));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void Resume_RejectsSourceAndTargetExisting_WithoutDeletingEither()
    {
        string root = MakeTempDir();
        try
        {
            string scriptsPath = Path.Combine(root, "config", "scripts.json");
            string usersPath = Path.Combine(root, "config", "users.json");
            string journalPath = Path.Combine(root, "config", "migrations", "v096-users.json");
            string source = Path.Combine(root, "data", "s", "Alice");
            string target = Path.Combine(root, "data", "s", "user-id");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "source.txt"), "source");
            File.WriteAllText(Path.Combine(target, "target.txt"), "target");
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            File.WriteAllText(journalPath, """
                {
                  "Version": 1,
                  "Phase": "Planned",
                  "StartedAt": "2026-08-24T12:00:00",
                  "Users": [{ "Id": "user-id", "Index": 0, "Name": "Alice", "Bindings": [{ "ScriptInstanceId": "s" }] }],
                  "Directories": [{ "ScriptId": "s", "OldName": "Alice", "UserId": "user-id", "Source": "SOURCE", "Target": "TARGET" }]
                }
                """.Replace("SOURCE", source.Replace("\\", "\\\\"), StringComparison.Ordinal)
                    .Replace("TARGET", target.Replace("\\", "\\\\"), StringComparison.Ordinal));

            Assert.Throws<InvalidOperationException>(() => UserModelMigration.Migrate(
                scriptsPath, usersPath, root + "\\data", journalPath, DateTime.Now));
            Assert.True(File.Exists(Path.Combine(source, "source.txt")));
            Assert.True(File.Exists(Path.Combine(target, "target.txt")));
            Assert.False(File.Exists(usersPath));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    private static string MakeTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "np-v096-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
