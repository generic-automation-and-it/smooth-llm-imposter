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
        string? managedAuthHeader = ApplyAuthentication(request, decision, credentialOverride, dialect, callerHeaders);
        EnsureAnthropicVersion(request, decision, credentialOverride, dialect);

        logger.LogDebug("Forwarding to {Provider} at {Target}", decision.Provider.Name, target);
        LogOutboundRequest(request, target, body, managedAuthHeader);

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
            // to a gateway-specific header (e.g. the LEGO codex gateway's `api-key`). The value format still
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

    // Auth headers whose secret value is masked in the Debug request dump so real keys never reach the log sink.
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "x-api-key",
    };

    // Debug-only dump of the exact request leaving the forwarder (method, target, every header that opencode/the
    // upstream will actually receive). Mirrors the Host's inbound dump so you can diff what the caller sent vs what
    // is forwarded — the suspect is a relayed caller header the upstream rejects. Off by default (Information); the
    // IsEnabled guard keeps it free when disabled. Auth secrets are masked (scheme + last 4 chars only).
    private void LogOutboundRequest(HttpRequestMessage request, string target, string? body, string? managedAuthHeader)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var headers = new StringBuilder();
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            // Mask the static auth headers and any provider-specific AuthHeader the managed secret was written
            // into, so a relocated credential (e.g. `api-key`) never reaches the log sink in the clear.
            bool sensitive = SensitiveHeaders.Contains(header.Key) ||
                (managedAuthHeader is not null && string.Equals(header.Key, managedAuthHeader, StringComparison.OrdinalIgnoreCase));
            string value = sensitive
                ? MaskSecretHeader(string.Join(", ", header.Value))
                : string.Join(", ", header.Value);
            headers.Append("\n  ").Append(header.Key).Append(": ").Append(value);
        }

        if (request.Content is not null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
            {
                headers.Append("\n  ").Append(header.Key).Append(": ").Append(string.Join(", ", header.Value));
            }
        }

        // Body is the exact post-transform payload sent to the upstream (Responses→Chat conversion already
        // applied). Logged in full at Debug to diagnose tool-shape/name rejections; no secrets live in the
        // body (auth is header-only, masked above). Temporary diagnostic — remove or gate further if noisy.
        logger.LogDebug(
            "Outbound {Method} {Target}\nHeaders:{Headers}\nBody: {Body}",
            request.Method, target, headers.ToString(), body ?? "(none)");
    }

    // Preserve the auth scheme prefix (e.g. "Bearer ") and the secret's last 4 chars; mask the rest. Short
    // secrets (≤4 chars) are fully masked so nothing recoverable is logged.
    private static string MaskSecretHeader(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        int spaceIndex = value.IndexOf(' ');
        string scheme = spaceIndex > 0 ? value[..(spaceIndex + 1)] : string.Empty;
        string secret = spaceIndex > 0 ? value[(spaceIndex + 1)..] : value;
        string tail = secret.Length > 4 ? secret[^4..] : string.Empty;

        return $"{scheme}***{tail}";
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
