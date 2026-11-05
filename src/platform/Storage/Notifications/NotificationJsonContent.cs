using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationJsonContent : HttpContent
{
    private sealed record Part(byte[] Literal, bool Image);
    private readonly IReadOnlyList<Part> _parts;
    private readonly NotificationAttachment? _image;
    private readonly long _length;

    private NotificationJsonContent(IReadOnlyList<Part> parts, NotificationAttachment? image)
    {
        _parts = parts; _image = image;
        _length = parts.Sum(part => part.Image ? checked(((image!.SizeBytes + 2) / 3) * 4) : part.Literal.LongLength);
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
    }

    internal static NotificationJsonContent Plain(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        using var parsed = StrictJson.Parse(bytes);
        return new([new(bytes, false)], null);
    }

    internal static NotificationJsonContent Generic(string template, string text, NotificationAttachment? image)
    {
        var parts = new List<Part>(); var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length == 0) return;
            parts.Add(new(Encoding.UTF8.GetBytes(literal.ToString()), false)); literal.Clear();
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["{text}"] = JsonSerializer.Serialize(text),
            ["{imageFileName}"] = JsonSerializer.Serialize(image?.FileName ?? ""),
            ["{imageContentType}"] = JsonSerializer.Serialize(image?.ContentType ?? "") };
        var tokens = values.Keys.Concat(["{imageBase64}", "{imageDataUri}"]).ToArray();
        long control = 0;
        for (var offset = 0; offset < template.Length;)
        {
            var token = tokens.FirstOrDefault(key => template.AsSpan(offset).StartsWith(key, StringComparison.Ordinal));
            if (token is null) { literal.Append(template[offset++]); control++; }
            else
            {
                offset += token.Length;
                if (token is "{imageBase64}" or "{imageDataUri}")
                {
                    literal.Append('"');
                    if (image is not null)
                    {
                        if (token == "{imageDataUri}") literal.Append("data:" + image.ContentType + ";base64,");
                        Flush(); parts.Add(new([], true));
                    }
                    literal.Append('"'); control += 100;
                }
                else { literal.Append(values[token]); control += values[token].Length; }
            }
            if (control > StrictJson.MaximumBytes) throw new IOException("notification_template_payload_limit");
        }
        Flush();
        var skeleton = parts.Where(part => !part.Image).SelectMany(part => part.Literal).ToArray();
        using var parsed = StrictJson.Parse(skeleton);
        return new(parts, image);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        WriteAsync(stream, CancellationToken.None);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        WriteAsync(stream, cancellationToken);
    protected override bool TryComputeLength(out long length) { length = _length; return true; }

    private async Task WriteAsync(Stream stream, CancellationToken cancellation)
    {
        foreach (var part in _parts)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!part.Image) await stream.WriteAsync(part.Literal, cancellation).ConfigureAwait(false);
            else
            {
                using var input = _image!.OpenRead();
                using var transform = new ToBase64Transform();
                using var output = new CryptoStream(stream, transform, CryptoStreamMode.Write, leaveOpen: true);
                await input.CopyToAsync(output, 65536, cancellation).ConfigureAwait(false);
                await output.FlushFinalBlockAsync(cancellation).ConfigureAwait(false);
            }
        }
    }
}
