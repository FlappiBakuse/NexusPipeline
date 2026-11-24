using System.IO.Pipes;
using System.Text.Json;
using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Extensibility.Protocol;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ProjectChunkReaderTests
{
    [Theory(DisplayName = "The SDK Rejects A Chunk Whose Frame Length Or Request Binding Differs")]
    [InlineData("offset", "execution_project_response_unbound")]
    [InlineData("fingerprint", "execution_project_response_unbound")]
    [InlineData("sequence", "execution_project_response_unbound")]
    [InlineData("length", "execution_project_response_invalid")]
    [InlineData("digest", "execution_project_response_invalid")]
    public async Task RejectsInvalidChunk(string fault, string reason)
    {
        var bootstrap = Bootstrap();
        await using var peer = ProjectPipe(bootstrap);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var admitted = AdmitAsync(peer, deadline.Token);
        await using var reader = await ExecutionProjectReader.ConnectAsync(bootstrap, deadline.Token);
        await admitted;

        var reading = reader.ReadAssetChunkAsync("asset", 1, 2, deadline.Token);
        var request = await ReadRequestAsync(peer, deadline.Token);
        Assert.Equal((1L, "asset", 1L, 2L), (request.Sequence, request.Kind, request.Offset!.Value, request.MaximumBytes!.Value));
        var response = new ExecutionProjectResponse
        {
            Fingerprint = request.Fingerprint, Sequence = request.Sequence, Kind = request.Kind, Offset = request.Offset,
            SizeBytes = 4, Sha256 = new string('b', 64), ContentBase64 = "AgM="
        };
        response = fault switch
        {
            "offset" => response with { Offset = 0 },
            "fingerprint" => response with { Fingerprint = new string('c', 64) },
            "sequence" => response with { Sequence = 2 },
            "length" => response with { ContentBase64 = "Ag==" },
            "digest" => response with { Sha256 = "invalid" },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        var encoded = ExecutionProviderFrames.Encode(response, ExecutionProviderFrames.MaximumBootstrapBytes);
        await ExecutionProviderFrames.WriteAsync(peer, encoded, ExecutionProviderFrames.MaximumBootstrapBytes, deadline.Token);
        var rejected = await Assert.ThrowsAsync<IOException>(() => reading);
        Assert.Equal(reason, rejected.Message);
        await Assert.ThrowsAsync<IOException>(() => reader.ManifestAsync(deadline.Token));
    }

    [Fact(DisplayName = "Cancelling An In-Flight File Chunk Closes The Original Project Channel")]
    public async Task CancelsInFlightChunk()
    {
        var bootstrap = Bootstrap();
        await using var peer = ProjectPipe(bootstrap);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var admitted = AdmitAsync(peer, deadline.Token);
        await using var reader = await ExecutionProjectReader.ConnectAsync(bootstrap, deadline.Token);
        await admitted;

        using var readingCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var reading = reader.ReadFileChunkAsync("resource", "pipeline.json", 0, 131072, readingCancellation.Token);
        var request = await ReadRequestAsync(peer, deadline.Token);
        Assert.Equal("file", request.Kind);
        Assert.Equal("pipeline.json", request.Path);
        readingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        await Assert.ThrowsAsync<IOException>(() => reader.ManifestAsync(deadline.Token));
    }

    private static NamedPipeServerStream ProjectPipe(ExecutionProviderBootstrap bootstrap) =>
        new(bootstrap.Project!.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task AdmitAsync(NamedPipeServerStream peer, CancellationToken cancellation)
    {
        await peer.WaitForConnectionAsync(cancellation);
        await ExecutionProviderFrames.WriteAsync(peer, new byte[] { 1 }, 1, cancellation);
    }

    private static async Task<ExecutionProjectRequest> ReadRequestAsync(NamedPipeServerStream peer, CancellationToken cancellation)
    {
        var bytes = await ExecutionProviderFrames.ReadAsync(peer, 65536, cancellation);
        return JsonSerializer.Deserialize<ExecutionProjectRequest>(bytes) ?? throw new IOException("project_fixture_request_missing");
    }

    private static ExecutionProviderBootstrap Bootstrap()
    {
        var nonce = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid().ToString("D");
        var fingerprint = new string('a', 64);
        var identity = new ExecutionProviderEventIdentity
        {
            InstanceId = id, ParentOperationId = id, ParentJournalId = id, RunId = id, AttemptId = id, ActivationId = id,
            StepId = id, ExtensionId = "fixture", EntryId = "main", PlanDigest = fingerprint, Nonce = nonce
        };
        return new()
        {
            Format = "nexus.execution.provider-bootstrap", Version = 1, Identity = identity, PipeName = "NexusExecutionProvider-" + nonce,
            Project = new() { Fingerprint = fingerprint, PipeName = "NexusExecutionProject-" + nonce },
            Upstream = new()
            {
                Path = "fixture.exe", SizeBytes = 0, Sha256 = fingerprint,
                Identity = new() { FileId = "0", VolumeSerial = "0" },
                ParentIdentity = new() { FileId = "1", VolumeSerial = "0" }, RuntimeFiles = []
            },
            UpstreamArguments = [], WorkingDirectory = Path.GetTempPath(), ConfigurationTargets = [],
            MaximumEvents = 2, AttemptOrdinal = 1, IdempotencyKey = fingerprint
        };
    }
}
