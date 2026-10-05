using NexusPipeline.Modules.Updates;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class UpdateTaskTests
{
    [Fact]
    public void UnsupportedJournalAndMarkerRemainDistinctFromMissingAndCannotBeRewritten()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-update-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "task.json");
        var task = UpdateTask.Create("apply", "0.16.15", Path.Combine(root, "staging"));
        try
        {
            Assert.Equal(UpdateFileState.Missing, UpdateTask.ReadState(file).State);
            string valid = System.Text.Json.JsonSerializer.Serialize(task);
            foreach (string invalid in new[]
            {
                "{}", "{broken", valid.Replace("\"CreatedAt\":", "\"createdAt\":"),
                valid.Replace("\"Phase\":\"ApplyRequested\"", "\"Phase\":\"future\""),
                valid.Replace("\"Mode\":\"apply\"", "\"Mode\":\"apply\",\"mode\":\"apply\""),
                System.Text.Json.JsonSerializer.Serialize(task with { CreatedAt = null }),
                System.Text.Json.JsonSerializer.Serialize(task with { Phase = UpdatePhase.AwaitingStartup }),
            })
            {
                File.WriteAllText(file, invalid);
                Assert.Equal(UpdateFileState.Unsupported, UpdateTask.ReadState(file).State);
                Assert.Throws<InvalidDataException>(() => task.Write(file));
                Assert.Throws<InvalidDataException>(() => task.Clear(file));
                Assert.Equal(invalid, File.ReadAllText(file));
            }
            File.WriteAllText(file, valid);
            Assert.Throws<InvalidDataException>(() => (task with { CreatedAt = task.CreatedAt!.Value.AddSeconds(1) }).Write(file));
            Assert.Equal(valid, File.ReadAllText(file));
            string marker = Path.Combine(root, "marker");
            Assert.Equal(UpdateFileState.Missing, UpdateApply.ReadVersionState(marker).State);
            foreach (string invalid in new[] { "", "unknown", "0.16.15\n0.16.14" })
            {
                File.WriteAllText(marker, invalid);
                Assert.Equal(UpdateFileState.Unsupported, UpdateApply.ReadVersionState(marker).State);
                Assert.Equal(invalid, File.ReadAllText(marker));
            }
            File.WriteAllText(marker, "0.16.15\n");
            Assert.Equal(UpdateFileState.Current, UpdateApply.ReadVersionState(marker).State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CommittedCleanupKeepsJournalUntilEveryOwnedResourceIsRemoved()
    {
        foreach (string interruption in new[] { "staging", "downloads", "backup", "worker", "containers", "marker", "journal" })
        {
            string root = Path.Combine(Path.GetTempPath(), "nxp-update-cleanup-" + Guid.NewGuid().ToString("N"));
            string update = Path.Combine(root, ".nxp-update"), staging = Path.Combine(update, "staging", "0.16.15.g1");
            string backup = Path.Combine(root, ".nxp-backup", "previous");
            Directory.CreateDirectory(staging); Directory.CreateDirectory(backup);
            string worker = Path.Combine(root, ".nxp-update-worker-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(worker, "worker");
            File.WriteAllText(Path.Combine(staging, "README.md"), "current");
            File.WriteAllText(Path.Combine(backup, "nexus-pipeline.exe"), "backup");
            File.WriteAllText(Path.Combine(root, ".nxp-version"), "0.16.15");
            string journal = Path.Combine(update, "task.json");
            var task = UpdateTask.Create("apply", "0.16.15", staging) with
            {
                Mode = "completed", Phase = UpdatePhase.Committed, TransactionId = Guid.NewGuid().ToString("N"),
                TargetImageHash = new string('a', 64), WorkerIdentity = new(42, DateTime.UtcNow, worker),
            };
            task.Write(journal);
            try
            {
                Assert.Throws<IOException>(() => UpdateCleanup.Complete(task, root, _ => false,
                    step => { if (step == interruption) throw new IOException("injected interruption"); }));
                if (interruption != "journal")
                {
                    Assert.Equal(task, UpdateTask.Read(journal));
                    UpdateCleanup.Complete(task, root, _ => false);
                }
                Assert.False(File.Exists(journal));
                Assert.False(File.Exists(worker));
                Assert.False(Directory.Exists(staging));
                Assert.False(Directory.Exists(backup));
                Assert.False(File.Exists(Path.Combine(root, ".nxp-version")));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public void RestartHandoffPersistsThroughWorkerJournalPhases()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-update-journal-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "task.json");
        try
        {
            string handoff = Guid.NewGuid().ToString("N");
            var task = UpdateTask.Create("apply", "0.16.15", Path.Combine(root, "staging")) with
            {
                RestartHandoffId = handoff,
            };
            task.Write(file);
            UpdateTask received = Assert.IsType<UpdateTask>(UpdateTask.Read(file));
            Assert.Equal(handoff, received.RestartHandoffId);

            received = received with { Phase = UpdatePhase.AwaitingStartup, TransactionId = Guid.NewGuid().ToString("N"),
                TargetImageHash = new string('a', 64), WorkerIdentity = new(42, DateTime.UtcNow, Path.Combine(root, "worker.exe")) };
            received.Write(file);
            Assert.Equal(handoff, UpdateTask.Read(file)?.RestartHandoffId);
            Assert.Equal(task.CreatedAt, UpdateTask.Read(file)?.CreatedAt);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InvalidRestartHandoffIsNotAdopted()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-update-journal-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "task.json");
        try
        {
            var task = UpdateTask.Create("apply", "0.16.15", Path.Combine(root, "staging")) with
            {
                RestartHandoffId = "not-an-id",
            };
            Assert.Throws<InvalidDataException>(() => task.Write(file));
            Directory.CreateDirectory(root);
            string bytes = System.Text.Json.JsonSerializer.Serialize(task);
            File.WriteAllText(file, bytes);
            Assert.Equal(UpdateFileState.Unsupported, UpdateTask.ReadState(file).State);
            Assert.Throws<InvalidDataException>(() => UpdateTask.Read(file));
            Assert.Equal(bytes, File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
