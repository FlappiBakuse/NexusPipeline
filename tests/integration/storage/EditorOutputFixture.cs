using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class EditorOutputFixture : IDisposable
{
    internal ConfigurationEditFixture Edit { get; }
    internal InstanceLease Lease { get; }
    internal ConfigurationEditorProcess Editor { get; }
    internal EditOutputRecorder Recorder { get; }
    internal string Executable => Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
    internal string ExecutableSha256 => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Executable)));

    internal EditorOutputFixture(EditWritePolicy? policy = null, bool directorySnapshot = false)
    {
        Edit = new(writePolicy: policy ?? new([""], [FileObjectKind.Missing, FileObjectKind.File, FileObjectKind.Directory]), directorySnapshot: directorySnapshot);
        Lease = InstanceLease.Acquire(Edit.Base.Instance, new());
        Editor = new(Edit.Base.Instance, Edit.Base.Database, new(), Lease, Edit.SessionId, "fixture");
        Recorder = new(Edit.Base.Instance, Edit.Base.Database, new(), Lease);
        Edit.InstallAll();
    }

    internal async Task WriteAsync(object[] operations, bool child = false)
    {
        var input = Path.Combine(Edit.Base.Root, "editor-input.json"); var report = Path.Combine(Edit.Base.Root, "editor-ready.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { operations, child }));
        Editor.Start(Executable, ExecutableSha256, ["configuration-editor-writer", input, report], Edit.Base.Root);
        Xunit.Assert.Equal("edited", (await OwnedCrashProcess.ReadReportAsync(report)).GetProperty("status").GetString());
    }

    internal ConfigurationEditCompletion Seal(StoppedConfigurationEditor stopped)
    {
        var changes = Edit.Session.Resources.Select(resource => new ConfigurationRevisionChange(resource.Address, resource.ExpectedRevision,
            Edit.Session.ExpectedBindingRevision, resource.Contract, Recorder.Seal(resource.JournalId, stopped), ConfigurationUpdateKind.Manual)).ToImmutableArray();
        return new(Edit.SessionId, 2, new(changes));
    }
    public void Dispose() { Editor.Dispose(); Lease.Dispose(); }
}
