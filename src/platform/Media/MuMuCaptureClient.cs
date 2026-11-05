using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Media;

[SupportedOSPlatform("windows")]
internal sealed class MuMuCaptureClient : IDisposable
{
    private readonly OwnedWorkerJob _helper;
    private readonly MuMuCaptureRequest _request;
    private readonly ImmutableBlobStore _blobs;
    private readonly CapturePngSpool _png;
    private readonly List<CaptureChildLease> _children = [];
    private readonly List<IDisposable> _held = [];
    private (CaptureChildLease Child, CaptureCommandObserved Document)? _pending;
    private bool _started, _disposed;

    internal MuMuCaptureClient(OwnedWorkerJob helper, MuMuCaptureRequest request, ImmutableBlobStore blobs, CapturePngSpool png)
    { _helper = helper; _request = request; _blobs = blobs; _png = png; }

    internal async Task<MuMuCaptureCompleted> RunAsync(Stream input, Stream output,
        Func<CaptureChildLease, Func<BlobReference, CancellationToken, Task>, CancellationToken, Task<BlobReference>> admit,
        Action<Observed> publish, CancellationToken cancellation)
    {
        if (_started || _disposed) throw MuMuCaptureProtocol.Invalid();
        _started = true; MuMuCaptureProtocol.Require(_request);
        await MuMuCaptureProtocol.WriteAsync(output, _request, cancellation).ConfigureAwait(false);
        var count = 0; var expectedReason = "execution_capture_frame_verified";
        for (var sequence = 1; sequence <= MuMuCaptureSequence.MaximumCommands; sequence++)
        {
            var ready = await MuMuCaptureProtocol.ReadAsync<MuMuChildReady>(input, cancellation).ConfigureAwait(false);
            MuMuCaptureProtocol.Require(ready, _request, sequence);
            if (_children.Any(child => child.JobName == ready.JobName || child.ProcessIdentity == ready.Process))
                throw MuMuCaptureProtocol.Invalid();
            var child = new CaptureChildLease(_helper, _request.Binding, sequence, ready.JobName, ready.Process);
            _children.Add(child);
            BlobReference? root = null;
            root = await admit(child, (acceptedRoot, token) => MuMuCaptureProtocol.WriteAsync(output,
                new MuMuChildAdmission(MuMuCaptureProtocol.AdmissionFormat, 1, _request.Binding, _request.Source,
                    sequence, ready.Process, acceptedRoot), token), cancellation).ConfigureAwait(false);
            if (root is null) throw MuMuCaptureProtocol.Invalid();
            var reply = await MuMuCaptureProtocol.ReadAsync<MuMuCommandReply>(input, cancellation).ConfigureAwait(false);
            MuMuCaptureProtocol.Require(reply, _request, sequence);
            byte[]? mapping = null;
            ExecutionCaptureOutput stdout;
            if (sequence == 2)
            {
                using var binary = new MuMuChunkStream(input, reply.StandardOutputBytes);
                await _png.ReadOutputAsync(binary, cancellation).ConfigureAwait(false);
                if (binary.FinishHash() != reply.StandardOutputSha256) throw MuMuCaptureProtocol.Invalid();
                stdout = new(reply.StandardOutputBytes, reply.StandardOutputSha256, []);
            }
            else
            {
                mapping = await ReadAsync(input, reply.StandardOutputBytes, MuMuCaptureSequence.MaximumInfoBytes,
                    reply.StandardOutputSha256, cancellation).ConfigureAwait(false);
                stdout = Save(mapping, MuMuCaptureSequence.MaximumInfoBytes);
            }
            var error = await ReadAsync(input, reply.StandardErrorBytes, MuMuCaptureSequence.MaximumErrorBytes,
                reply.StandardErrorSha256, cancellation).ConfigureAwait(false);
            var document = new CaptureCommandObserved(ExecutionCaptureCodec.Format + "-command-observed", 1,
                _request.Binding, root, reply.Completion, reply.ExitCode, stdout,
                Save(error, MuMuCaptureSequence.MaximumErrorBytes), reply.Stopped);
            child.RequireObserved(document);
            _pending = (child, document); publish(Observed.Take(this)); count++;
            if (reply.Completion != "natural-root-exit-and-eof-before-stop" || reply.ExitCode != 0)
            { expectedReason = "execution_capture_helper_failed"; break; }
            if (mapping is not null)
            {
                try
                {
                    var utf8 = new UTF8Encoding(false, true);
                    if (!string.IsNullOrWhiteSpace(utf8.GetString(error))) throw MuMuCaptureProtocol.Invalid();
                    MuMuInfoParser.RequireCaptureMapping(utf8.GetString(mapping), _request.Index, _request.Endpoint);
                }
                catch (Exception failure) when (failure is IOException or System.Text.Json.JsonException or DecoderFallbackException)
                { expectedReason = "execution_capture_mapping_unverified"; break; }
            }
        }
        var complete = await MuMuCaptureProtocol.ReadAsync<MuMuCaptureCompleted>(input, cancellation).ConfigureAwait(false);
        if (complete.Format != MuMuCaptureProtocol.CompletedFormat || complete.Version != 1 ||
            complete.Binding != _request.Binding || complete.Source != _request.Source || complete.Commands != count ||
            complete.ReasonCode != expectedReason || complete.Outcome !=
            (expectedReason == "execution_capture_frame_verified" ? "completed" : "unavailable") ||
            complete.Outcome == "completed" && count != 3) throw MuMuCaptureProtocol.Invalid();
        return complete;
    }

    private ExecutionCaptureOutput Save(byte[] bytes, int maximum) => new(bytes.LongLength,
        Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length == 0 ? [] :
        JudgementFrameParts.Write(_blobs, bytes, maximum, reference => _held.Add(_blobs.OpenVerified(reference))));

    private static async Task<byte[]> ReadAsync(Stream input, long size, int maximum, string hash, CancellationToken cancellation)
    {
        using var bytes = new MemoryStream();
        await MuMuCaptureProtocol.ReadBytesAsync(input, size, maximum,
            chunk => bytes.WriteAsync(chunk, cancellation).AsTask(), cancellation).ConfigureAwait(false);
        var result = bytes.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(result)) != hash) throw MuMuCaptureProtocol.Invalid();
        return result;
    }

    internal sealed class Observed
    {
        private readonly MuMuCaptureClient _owner;
        private readonly CaptureChildLease _child;
        private readonly CaptureCommandObserved _document;
        private Observed(MuMuCaptureClient owner, CaptureChildLease child, CaptureCommandObserved document)
        { _owner = owner; _child = child; _document = document; }
        internal static Observed Take(MuMuCaptureClient owner)
        {
            var pending = owner._pending ?? throw MuMuCaptureProtocol.Invalid(); owner._pending = null;
            return new(owner, pending.Child, pending.Document);
        }
        internal T Use<T>(OwnedWorkerJob actual, ExecutionCaptureBinding binding, BlobReference source,
            BlobReference brokerRoot, ProcessIdentity broker, Func<CaptureCommandObserved, T> publish)
        {
            if (_owner._disposed || !ReferenceEquals(actual, _owner._helper) || binding != _owner._request.Binding ||
                source != _owner._request.Source || brokerRoot != _owner._request.BrokerRoot || broker != _owner._request.Broker ||
                !_owner._children.Contains(_child)) throw MuMuCaptureProtocol.Invalid();
            _child.RequireObserved(_document); return publish(_document);
        }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        for (var index = _children.Count - 1; index >= 0; index--) _children[index].Dispose();
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
    }
}
