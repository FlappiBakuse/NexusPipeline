using NexusPipeline.Updater;
using NexusPipeline.Modules.Updates;
using Xunit;
namespace NexusPipeline.Tests.Updates;
public sealed class UpdateWindowActionTests
{
    [Fact]
    public void PrimaryActionFollowsRealReadinessAndCancellationWithoutGuessingUnknownStates()
    {
        UpdateWindowState Snapshot(string state, bool canDownload = true) => new("0.17.4", "0.17.5", state, canDownload, 100, "");
        Assert.False(UpdateButtons.Map(null, false).Enabled);
        Assert.False(UpdateButtons.Map(Snapshot("future-state"), false).Enabled);
        Assert.False(UpdateButtons.Map(Snapshot("idle", false), false).Enabled);
        Assert.Equal(PrimaryAction.Download, UpdateButtons.Map(Snapshot("idle"), false).Action);
        Assert.Equal(PrimaryAction.CancelDownload, UpdateButtons.Map(Snapshot("downloading"), false).Action);
        Assert.False(UpdateButtons.Map(Snapshot("cancelling"), false).Enabled);
        Assert.Equal(PrimaryAction.ApplyRestart, UpdateButtons.Map(Snapshot("ready"), false).Action);
        foreach (string state in new[] { "checking", "cancelling", "applying", "applypending", "recoverypending", "manualadoptionpending" }) Assert.False(UpdateButtons.Map(Snapshot(state), false).Enabled);
        foreach (string state in new[] { "idle", "downloading", "ready" }) Assert.False(UpdateButtons.Map(Snapshot(state), true).Enabled);
        var failure = new UpdateStatusSnapshot(UpdateState.Idle, null, 0, 0, "fixture response with credentials", "0.17.4", "0.17.5", true, "prerelease", true, "notes", true);
        Assert.Equal("update_failed", UpdateSessions.Project(failure).Message);
        Assert.Equal("notes", UpdateSessions.Project(failure with { Error = "" }).Message);
        Assert.Equal("update.cleanup_preserved", UpdateSessions.Project(failure with { Error = "update.cleanup_preserved: conflict" }).Message);
    }
}
