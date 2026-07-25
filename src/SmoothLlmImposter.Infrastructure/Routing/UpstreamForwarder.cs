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
/// <see cref="CancellationToken"/>: SSE streams routinely outlive the standard resilience timeouts. A targeted
/// retry handler covers pre-response outbound transport failures.
/// </remarks>
internal sealed class UpstreamForwarder(IHttpClientFactory httpClientFactory, ILogger<UpstreamForwarder> logger)
    : IUpstreamForwarder
{
    internal const string HttpClientName = "imposter-upstream";
    private const string DefaultAnthropicVersion = "2023-06-01";
    private const string SessionHeaderName = "x-opencode-session";

    public async Task<HttpResponseMessage> SendAsync(
        RouteDecision decision,
        RouteCredentialOverride? credentialOverride,
        ApiDialect dialect,
        HttpMethod method,
        string? body,
        string path,
        string? queryString,
        CallerHeaders callerHeaders,
        SessionIdentity? sessionIdentity,
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
        string? managedAuthHeader = ApplyAuthentication(request, decision, credentialOverride, dialect, callerHeaders);
        EnsureAnthropicVersion(request, decision, credentialOverride, dialect);
        ApplySessionIdentity(request, decision, sessionIdentity);

        logger.LogDebug("Forwarding to {Provider} at {Target}", decision.Provider.Name, target);

        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        // Headers-read completion keeps body-stream failures outside the retry scope, avoiding partial replay.
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    // Headers the transport owns or that are unsafe to relay verbatim. Auth headers are excluded here and
    // handled by ApplyAuthentication; content headers belong on HttpContent and the body may be rewritten.
    // session_id/x-opencode-session are passthrough-transparent (HLD 009): on default routes the caller's
    // own values reach the upstream verbatim; on an opted-in imposter route with a resolved identity,
    // ApplySessionIdentity drops them and writes the resolved identity; when the resolver returns
    // SessionIdentity.None (e.g. no headers, no body marker, no stable fingerprint), caller headers are
    // forwarded verbatim to keep the route byte-transparent.
    private static readonly HashSet<string> NonForwardableHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Authorization", "x-api-key",
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Expect", "Accept-Encoding",
        "Content-Length", "Content-Type", "Content-Encoding", "Content-Language",
        "Content-Location", "Content-MD5", "Content-Range",
    };

    // Caller identity headers that contradict a managed credential and are stripped only when the forwarder
    // applies a provider/override secret. They assert a specific upstream account (Codex sends chatgpt-account-id
    // alongside its own Bearer); relayed to an imposter upstream authenticated with a different key, the upstream
    // honours the header over the key and 401s. Withheld on managed auth, kept on key-less passthrough.
    private static readonly string[] ManagedAuthIdentityHeaders = ["chatgpt-account-id"];

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

    // Returns the header name the managed credential was written into (so the Debug dump can mask a
    // non-standard AuthHeader carrying the secret), or null on key-less passthrough (where only the static
    // Authorization/x-api-key are written, already masked).
    private static string? ApplyAuthentication(
        HttpRequestMessage request,
        RouteDecision decision,
        RouteCredentialOverride? credentialOverride,
        ApiDialect dialect,
        CallerHeaders callerHeaders)
    {
        string? secret = credentialOverride?.Secret ?? decision.Provider.Secret;

        if (!string.IsNullOrEmpty(secret))
        {
            // The provider/override credential is now the upstream identity, so drop any caller header that
            // asserts a *different* identity — e.g. Codex's chatgpt-account-id, which an OpenAI-compatible
            // gateway (opencode) honours over the Bearer key and 401s on when it doesn't match its account.
            // These were relayed verbatim by ForwardCallerHeaders; remove them here so managed auth isn't
            // contradicted. Passthrough keeps them: the caller's own credential + identity are a matched pair.
            foreach (string conflicting in ManagedAuthIdentityHeaders)
            {
                request.Headers.Remove(conflicting);
            }

            // Scheme is decoupled from dialect and resolved by the shared Domain resolver (also used by the
            // router's log so the two cannot drift): a stored credential's scheme, else the provider's
            // configured scheme, else the dialect default; the HLD 003 override forces Bearer regardless.
            // Headers are only ever added, so x-api-key is inherently never sent when Bearer is forced.
            CredentialAuthScheme scheme = UpstreamAuthResolver.ResolveScheme(
                dialect,
                decision.Provider.AuthScheme,
                credentialOverride?.AuthScheme,
                credentialOverride?.ForceBearer ?? false);

            // The scheme's default header (Authorization/x-api-key) unless the provider relocates the value
            // to a gateway-specific header (e.g. the MyCompany Gateway's `api-key`). The value format still
            // follows the scheme, so a Bearer credential in `api-key` is `api-key: Bearer <token>`.
            string headerName = decision.Provider.AuthHeader ?? UpstreamAuthResolver.DefaultHeaderNameFor(scheme);
            ApplyScheme(request, scheme, secret, headerName);

            return headerName;
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

        return null;
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

    // HLD 009: stamp x-opencode-session once on matched opted-in imposter routes. Drop caller-relayed
    // session_id and x-opencode-session first (ForwardCallerHeaders copies them now that they are
    // passthrough-transparent) so the resolved identity is the sole write — mirrors the managed-auth
    // drop-then-write pattern. The raw value is never logged at Information level.
    private static void ApplySessionIdentity(
        HttpRequestMessage request,
        RouteDecision decision,
        SessionIdentity? sessionIdentity)
    {
        if (!SessionForwardingPolicy.IsOptedIn(decision) ||
            sessionIdentity is null ||
            !sessionIdentity.HasValue)
        {
            return;
        }

        request.Headers.Remove("session_id");
        request.Headers.Remove(SessionHeaderName);
        request.Headers.TryAddWithoutValidation(SessionHeaderName, sessionIdentity.Value);
    }

    // Writes the credential into headerName using the value format the scheme dictates: Bearer prepends
    // "Bearer " (idempotent — a secret already carrying the prefix is not double-prefixed), ApiKey uses the
    // raw token. headerName is the scheme's default (Authorization/x-api-key) unless the provider relocates
    // it via AuthHeader. Any caller-relayed header of that name is dropped first so managed auth is the sole
    // value (the default headers are already withheld by NonForwardableHeaders; a custom name may not be).
    private static void ApplyScheme(HttpRequestMessage request, CredentialAuthScheme scheme, string secret, string headerName)
    {
        string value = scheme == CredentialAuthScheme.Bearer
            ? secret.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? secret : $"Bearer {secret}"
            : secret;

        request.Headers.Remove(headerName);
        request.Headers.TryAddWithoutValidation(headerName, value);
    }
}
