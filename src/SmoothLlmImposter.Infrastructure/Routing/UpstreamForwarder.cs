using System.Text;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Infrastructure.Routing;

/// <summary>
/// Forwards the request to the resolved upstream and returns the live response, read headers-first so SSE
/// bodies stream through. Acts as a transparent proxy: the caller's inbound headers are relayed verbatim
/// (minus hop-by-hop and content headers, which the transport owns), and the body is unchanged except for
/// imposter caching/model rewrites done upstream. The <b>only</b> header the forwarder manages is auth —
/// the caller's own credential passes through on key-less passthrough, or is replaced by the provider key /
/// stored credential / force-Bearer override.
/// </summary>
/// <remarks>
/// The named client uses an infinite <see cref="HttpClient.Timeout"/> and relies on the caller's
/// <see cref="CancellationToken"/>: SSE streams routinely outlive the standard resilience timeouts, and
/// a half-streamed POST cannot be safely retried, so no standard resilience handler is attached.
/// </remarks>
internal sealed class UpstreamForwarder(IHttpClientFactory httpClientFactory, ILogger<UpstreamForwarder> logger)
    : IUpstreamForwarder
{
    internal const string HttpClientName = "imposter-upstream";
    private const string DefaultAnthropicVersion = "2023-06-01";

    public async Task<HttpResponseMessage> SendAsync(
        RouteDecision decision,
        RouteCredentialOverride? credentialOverride,
        ApiDialect dialect,
        HttpMethod method,
        string? body,
        string path,
        string? queryString,
        CallerHeaders callerHeaders,
        CancellationToken cancellationToken)
    {
        Uri baseUrl = credentialOverride?.BaseUrlOverride ?? decision.Provider.BaseUrl;
        string target = baseUrl.AbsoluteUri.TrimEnd('/') + path + (queryString ?? string.Empty);

        using var request = new HttpRequestMessage(method, target);

        // Body-less requests (e.g. GET /v1/models discovery probes) carry no content.
        if (!string.IsNullOrEmpty(body))
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        // Proxy the caller's headers through unchanged (minus hop-by-hop/content/auth), then manage auth only.
        ForwardCallerHeaders(request, callerHeaders);
        ApplyAuthentication(request, decision, credentialOverride, dialect, callerHeaders);
        EnsureAnthropicVersion(request, decision, credentialOverride, dialect);

        logger.LogDebug("Forwarding to {Provider} at {Target}", decision.Provider.Name, target);

        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    // Headers the transport owns or that are unsafe to relay verbatim. Auth headers are excluded here and
    // handled by ApplyAuthentication; content headers belong on HttpContent and the body may be rewritten.
    private static readonly HashSet<string> NonForwardableHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Authorization", "x-api-key",
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Expect", "Accept-Encoding",
        "Content-Length", "Content-Type", "Content-Encoding", "Content-Language",
        "Content-Location", "Content-MD5", "Content-Range",
    };

    private static void ForwardCallerHeaders(HttpRequestMessage request, CallerHeaders callerHeaders)
    {
        foreach (KeyValuePair<string, IReadOnlyList<string>> header in callerHeaders.Items)
        {
            if (NonForwardableHeaders.Contains(header.Key))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static void ApplyAuthentication(
        HttpRequestMessage request,
        RouteDecision decision,
        RouteCredentialOverride? credentialOverride,
        ApiDialect dialect,
        CallerHeaders callerHeaders)
    {
        string? secret = credentialOverride?.Secret ?? decision.Provider.Secret;

        if (!string.IsNullOrEmpty(secret))
        {
            // Scheme is decoupled from dialect and resolved by the shared Domain resolver (also used by the
            // router's log so the two cannot drift): a stored credential's scheme, else the provider's
            // configured scheme, else the dialect default; the HLD 003 override forces Bearer regardless.
            // Headers are only ever added, so x-api-key is inherently never sent when Bearer is forced.
            CredentialAuthScheme scheme = UpstreamAuthResolver.ResolveScheme(
                dialect,
                decision.Provider.AuthScheme,
                credentialOverride?.AuthScheme,
                credentialOverride?.ForceBearer ?? false);
            ApplyScheme(request, scheme, secret);

            return;
        }

        // Key-less passthrough: forward the caller's own credential verbatim so the router still authenticates.
        // A matched imposter route forwards no caller auth — its (here empty) configured key governs instead.
        if (!decision.IsImposter)
        {
            if (callerHeaders.Get("Authorization") is { Count: > 0 } authorization)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            if (callerHeaders.Get("x-api-key") is { Count: > 0 } apiKey)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            }
        }
    }

    private static void EnsureAnthropicVersion(
        HttpRequestMessage request,
        RouteDecision decision,
        RouteCredentialOverride? credentialOverride,
        ApiDialect dialect)
    {
        // The caller's own anthropic-version is already forwarded by ForwardCallerHeaders and is left
        // untouched. Only supply a value when the caller omitted it, so the upstream still gets a required
        // header: a configured override/provider version if present, otherwise the documented default.
        if (dialect != ApiDialect.Anthropic || request.Headers.Contains("anthropic-version"))
        {
            return;
        }

        request.Headers.TryAddWithoutValidation(
            "anthropic-version",
            credentialOverride?.AnthropicVersion ?? decision.Provider.AnthropicVersion ?? DefaultAnthropicVersion);
    }

    private static void ApplyScheme(HttpRequestMessage request, CredentialAuthScheme scheme, string secret)
    {
        switch (scheme)
        {
            case CredentialAuthScheme.Bearer:
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");
                break;
            case CredentialAuthScheme.ApiKey:
                request.Headers.TryAddWithoutValidation("x-api-key", secret);
                break;
        }
    }
}
