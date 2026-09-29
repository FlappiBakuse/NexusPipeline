using NexusPipeline.Modules.Updates;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class UpdateTaskTests
{
    [Fact]
    public void RestartHandoffPersistsThroughWorkerJournalPhases()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-update-journal-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "task.json");
        try
        {
            string handoff = Guid.NewGuid().ToString("N");
            var task = new UpdateTask("apply", "0.16.12", Path.Combine(root, "staging"), UpdatePhase.ApplyRequested)
            {
                RestartHandoffId = handoff,
            };
            task.Write(file);
            UpdateTask received = Assert.IsType<UpdateTask>(UpdateTask.Read(file));
            Assert.Equal(handoff, received.RestartHandoffId);

            received = received with { Phase = UpdatePhase.AwaitingStartup };
            received.Write(file);
            Assert.Equal(handoff, UpdateTask.Read(file)?.RestartHandoffId);
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
            var task = new UpdateTask("apply", "0.16.12", Path.Combine(root, "staging"), UpdatePhase.ApplyRequested)
            {
                RestartHandoffId = "not-an-id",
            };
            task.Write(file);
            Assert.Null(UpdateTask.Read(file));
            Assert.True(File.Exists(file));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
