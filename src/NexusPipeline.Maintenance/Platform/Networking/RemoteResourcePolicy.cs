using System.Net;

namespace NexusPipeline.Platform.Networking;

/// <summary>
/// Product-neutral HTTP policy executor. Trust decisions are supplied by the
/// owning module through <see cref="RemoteResourceRules"/>.
/// </summary>
internal sealed class RemoteResourcePolicy
{
    private readonly RemoteResourceRules _rules;

    internal RemoteResourcePolicy(RemoteResourceRules rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    internal Uri SourceUri => _rules.SourceUri;

    internal string? ValidateUri(Uri uri) => _rules.Validate(uri);

    internal string? ValidateRedirectDestination(Uri uri) => _rules.Validate(uri);

    internal bool IsAllowedHost(string host) => _rules.IsAllowedHost(host);

    /// <summary>发送 GET 并手动跟随重定向；每一跳都经过调用方提供的规则。</summary>
    internal async Task<HttpResponseMessage> GetAsync(
        HttpClient http,
        Uri uri,
        string userAgent,
        CancellationToken token,
        Action<HttpRequestMessage>? configureRequest = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(uri);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_rules.Timeout);
        Uri current = uri;
        for (int redirect = 0; redirect <= _rules.MaxRedirects; redirect++)
        {
            string? validationError = redirect == 0
                ? _rules.ValidateInitial(current)
                : _rules.Validate(current);
            if (validationError is not null)
            {
                throw new InvalidDataException(validationError);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            configureRequest?.Invoke(request);
            HttpResponseMessage response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is
                HttpStatusCode.MultipleChoices
                or HttpStatusCode.MovedPermanently
                or HttpStatusCode.Found
                or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect)
            {
                Uri? next = response.Headers.Location;
                response.Dispose();
                if (next is null)
                {
                    throw new InvalidDataException("远程资源重定向缺少目标地址");
                }
                if (!next.IsAbsoluteUri)
                {
                    next = new Uri(current, next);
                }
                if (redirect == _rules.MaxRedirects)
                {
                    throw new InvalidDataException("远程资源重定向次数超过上限");
                }
                string? redirectError = _rules.Validate(next);
                if (redirectError is not null)
                {
                    throw new InvalidDataException(redirectError);
                }
                current = next;
                continue;
            }
            return response;
        }
        throw new InvalidDataException("远程资源重定向失败");
    }
}
