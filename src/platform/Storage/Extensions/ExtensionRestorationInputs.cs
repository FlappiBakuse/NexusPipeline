using System.Globalization;
using System.Security.Cryptography;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ExtensionRestorationInputs
{
    internal static IDisposable Hold(RuntimeInstance instance, ContractReader contracts, BoundExtensionPayload bound)
    {
        var held = new List<IDisposable>();
        try
        {
            var references = PayloadSourceCodec.References(bound.Source)
                .Concat(bound.Physical.Select(item => item.Intent.Artifact))
                .Concat(bound.Restoration.Select(item => item.Intent.Artifact))
                .Prepend(bound.PlanReference).Prepend(bound.Plan.ExtensionSource!).Distinct().ToArray();
            if (references.Length > checked(2 * 4096 + 32)) throw new IOException("extension_restoration_content_limit");
            var blobs = new ImmutableBlobStore(instance, contracts);
            foreach (var reference in references) held.Add(blobs.OpenVerified(reference));
            foreach (var document in bound.History)
            {
                var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals",
                    document.JournalId.ToString("D"), document.Revision.ToString("D10", CultureInfo.InvariantCulture) + ".json"));
                var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                held.Add(input); var expected = document.Export();
                if (input.Length != expected.Length || !SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(expected)))
                    throw new IOException("extension_restoration_history_changed");
            }
            return new Held(held);
        }
        catch { for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose(); throw; }
    }

    private sealed class Held(List<IDisposable> values) : IDisposable
    {
        private List<IDisposable>? _values = values;
        public void Dispose()
        {
            var actual = Interlocked.Exchange(ref _values, null);
            if (actual is not null) for (var i = actual.Count - 1; i >= 0; i--) actual[i].Dispose();
        }
    }
}
