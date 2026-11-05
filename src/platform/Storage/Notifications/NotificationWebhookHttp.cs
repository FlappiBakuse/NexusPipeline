using System.Net.Http.Headers;
using System.Text;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed record NotificationHttpReply(int Status, string Body)
{
    internal bool IsSuccessful => Status is >= 200 and <= 299;
}

internal sealed class NotificationWebhookHttp(HttpClient client)
{
    internal async Task<NotificationHttpReply> PostAsync(Uri target, HttpContent body, string? bearer, CancellationToken cancellation)
    {
        RequireTarget(target);
        using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = body };
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        cancellation.ThrowIfCancellationRequested();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (count == 0) break;
            if (bytes.Length + count > StrictJson.MaximumBytes) throw new IOException("notification_response_limit");
            await bytes.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
        }
        return new((int)response.StatusCode, new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length)));
    }

    internal static void RequireTarget(Uri target)
    {
        if (!target.IsAbsoluteUri || target.Scheme is not ("http" or "https") || target.UserInfo.Length != 0 || target.Fragment.Length != 0)
            throw new IOException("notification_endpoint_invalid");
    }
}
