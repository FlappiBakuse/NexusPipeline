using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class MuMuDiscoveryFixture : IDisposable
{
    internal ExecutionAdmissionFixture Base { get; } = new();
    internal string Manager { get; }
    internal string Marker { get; }

    internal MuMuDiscoveryFixture(string mode = "valid")
    {
        try
        {
            var original = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_image_missing");
            var image = ExecutionHookImages.Capture(original, runtime: true);
            var directory = Path.Combine(Base.Source.Root, "mumu manager 空格");
            Directory.CreateDirectory(directory);
            Manager = Path.Combine(directory, Path.GetFileName(original));
            File.Copy(original, Manager);
            foreach (var asset in image.RuntimeFiles)
            {
                var target = Path.Combine(directory, asset.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(Path.GetDirectoryName(original)!, asset.RelativePath), target);
            }
            File.WriteAllBytes(Path.Combine(directory, "mumu-discovery-fixture.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { mode, port = 16384 }));
            Marker = Path.Combine(directory, "mumu-discovery");
        }
        catch { Base.Dispose(); throw; }
    }

    internal PreparedMuMuDiscovery Prepare(string endpoint = "127.0.0.1:16384") =>
        PreparedMuMuDiscovery.Prepare(Base.Source.Instance, new(), Base.Source.Lease, Base.Resources, Manager, endpoint);

    public void Dispose() => Base.Dispose();
}
