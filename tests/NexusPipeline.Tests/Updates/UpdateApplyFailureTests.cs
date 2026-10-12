using System.Text.Json;
using NexusPipeline.ControlPlane.Mcp;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class UpdateApplyFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nxp-update-results-" + Guid.NewGuid().ToString("N"));
    private string Write(string version, bool success, DateTimeOffset at, string code = "apply_failed")
    {
        string id = Guid.NewGuid().ToString("N"), directory = Path.Combine(_root, ".nxp", "state", "updates");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, id + ".result.json");
        JsonUtil.WriteAtomic(path, JsonSerializer.Serialize(new { TransactionId = id, Version = version, Succeeded = success, Code = code, AtUtc = at,
            DesktopResumeIntent = new { SchemaVersion = 1, Mode = "show" } }));
        return path;
    }

    [Fact]
    public void LatestFailureSurvivesServiceRestartAndHasThePublicProjection()
    {
        string version = "99.0.0";
        Write(version, false, DateTimeOffset.UtcNow, "payload_file_missing");
        var service = new UpdateService(() => new(), _root, () => true, () => true);
        var failure = Assert.IsType<UpdateApplyFailure>(service.GetStatus().LastApplyFailure);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(failure.ToPublicView()));
        Assert.Equal(version, json.RootElement.GetProperty("targetVersion").GetString());
        Assert.Equal("payload_file_missing", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(failure, new UpdateService(() => new(), _root, () => true, () => true).GetStatus().LastApplyFailure);
        Assert.NotNull(json.RootElement.GetProperty("transactionId").GetString());
        Assert.True(json.RootElement.GetProperty("atUtc").TryGetDateTimeOffset(out _));
        var automation = new UpdateAutomationService(() => new(), service.GetStatus,
            _ => Task.FromResult(service.GetStatus()), _ => UpdateDownloadResult.Started(),
            (_, _) => UpdateApplyResult.Ok(false), _ => throw new InvalidOperationException(), () => { });
        var query = new StatusToolQuery(null!, null!, null!, null!, null!, null!, service, automation, null!);
        using var mcp = JsonDocument.Parse(JsonSerializer.Serialize(query.BuildUpdateStatus()));
        Assert.Equal(json.RootElement.GetRawText(), mcp.RootElement.GetProperty("lastApplyFailure").GetRawText());
    }

    [Fact]
    public void SuccessAndInstalledTargetSuppressOldFailures()
    {
        var now = DateTimeOffset.UtcNow;
        Write("0.17.2", false, now);
        Assert.Null(UpdateApplyFailure.ReadLatest(_root, "0.17.2"));
        Write("99.0.0", false, now.AddSeconds(1));
        Assert.NotNull(UpdateApplyFailure.ReadLatest(_root, "0.17.2"));
        Write("99.0.0", true, now.AddSeconds(2));
        Assert.Null(UpdateApplyFailure.ReadLatest(_root, "0.17.2"));
    }

    [Fact]
    public void InvalidAndUnknownReceiptsRemainByteIdenticalAndDoNotHideValidFailure()
    {
        string valid = Write("99.0.0", false, DateTimeOffset.UtcNow);
        var directory = Path.GetDirectoryName(valid)!;
        string invalid = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".result.json");
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("{\"unknown\":true,\"unknown\":false}");
        File.WriteAllBytes(invalid, bytes);
        Assert.NotNull(UpdateApplyFailure.ReadLatest(_root, "0.17.2"));
        Assert.Equal(bytes, File.ReadAllBytes(invalid));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
