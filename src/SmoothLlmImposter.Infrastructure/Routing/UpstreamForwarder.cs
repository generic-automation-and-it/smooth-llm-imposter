using System.Text;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.GithubCopilot;
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
/// retry handler covers pre-response outbound transport failures, bounded by a per-attempt header timeout
/// whose base comes from the route (<c>TimeoutSeconds</c>) or, unset, the global default.
/// </remarks>
internal sealed class UpstreamForwarder(IHttpClientFactory httpClientFactory, ILogger<UpstreamForwarder> logger)
    : IUpstreamForwarder
{
    internal const string HttpClientName = "imposter-upstream";

    /// <summary>
    /// Managed-Copilot transport. Same resilience pipeline as <see cref="HttpClientName"/> but with automatic
    /// redirects disabled, so a redirect can never carry the GitHub token to another origin (AC10/R4).
    /// </summary>
    internal const string CopilotHttpClientName = "imposter-upstream-copilot";
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
        CancellationToken cancellationToken,
        bool useHeaderAllowlist = false,
        string? allowlistAccept = null)
    {
        Uri baseUrl = credentialOverride?.BaseUrlOverride ?? decision.Provider.BaseUrl;

        // R2/R4: a managed Copilot provider forwards only the same-dialect completion surface, at an
        // account-discovered GitHub origin. Anything else is refused before the token is attached.
        GithubCopilotProfile? copilotProfile = ResolveCopilotProfile(decision);
        string effectivePath = copilotProfile is null ? path : ApplyCopilotPathPolicy(decision, dialect, method, path);
        if (copilotProfile is not null && !GithubCopilotTransportPolicy.IsAllowedOrigin(baseUrl.AbsoluteUri))
        {
            throw new RoutingException(
                $"Provider '{decision.Provider.Name}' has a base URL that is not an allowed GitHub Copilot HTTPS origin; refusing to send a managed credential there.",
                statusCode: 500);
        }

        string target = baseUrl.AbsoluteUri.TrimEnd('/') + effectivePath + (queryString ?? string.Empty);

        using var request = new HttpRequestMessage(method, target);

        // Body-less requests (e.g. GET /v1/models discovery probes) carry no content.
        if (!string.IsNullOrEmpty(body))
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        // HLD 016 D20: a bridged Anthropic Messages-to-Chat route builds its header set from an allowlist
        // instead of relaying the caller's headers — nothing the caller sent (cookie, user-agent,
        // anthropic-*, x-stainless-*, x-api-key) may reach a third-party Chat host.
        if (useHeaderAllowlist)
        {
            request.Headers.TryAddWithoutValidation("accept", allowlistAccept ?? "application/json");
            ApplyAuthentication(request, decision, credentialOverride, dialect, callerHeaders);
            ApplySessionIdentity(request, decision, sessionIdentity);
        }
        else
        {
            // Proxy the caller's headers through unchanged (minus hop-by-hop/content/auth), then manage auth only.
            ForwardCallerHeaders(request, callerHeaders);
            string? managedAuthHeader = ApplyAuthentication(request, decision, credentialOverride, dialect, callerHeaders);
            EnsureAnthropicVersion(request, decision, credentialOverride, dialect);
            ApplySessionIdentity(request, decision, sessionIdentity);
        }
        if (copilotProfile is not null)
        {
            ApplyCopilotHeaders(request, dialect, GithubCopilotRequestHints.Resolve(dialect, callerHeaders, body));
        }

        // HLD 012: hand the route's base timeout to the resilience handler, which scales it per attempt.
        // Unstamped requests fall back to the global default inside the timeout strategy.
        if (decision.Provider.TimeoutSeconds is int timeoutSeconds && timeoutSeconds > 0)
        {
            request.Options.Set(DependencyInjection.UpstreamTimeoutSecondsKey, timeoutSeconds);
        }

        logger.LogDebug("Forwarding to {Provider} at {Target}", decision.Provider.Name, target);

        HttpClient client = httpClientFactory.CreateClient(
            copilotProfile is null ? HttpClientName : CopilotHttpClientName);
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

    internal static GithubCopilotProfile? ResolveCopilotProfile(RouteDecision decision) =>
        GithubCopilotProfileKey.TryParse(decision.Provider.CredentialProviderName, out GithubCopilotProfile? profile)
            ? profile
            : null;

    // Copilot serves Anthropic Messages at /v1/messages and OpenAI Responses at /responses only. Chat
    // Completions and every other path are rejected before forwarding — the proxy adds no cross-dialect bridge.
    private static string ApplyCopilotPathPolicy(RouteDecision decision, ApiDialect dialect, HttpMethod method, string path)
    {
        if (method == HttpMethod.Post && GithubCopilotTransportPolicy.TryResolveUpstreamPath(dialect, path, out string upstreamPath))
        {
            return upstreamPath;
        }

        throw new RoutingException(
            $"Provider '{decision.Provider.Name}' is a GitHub Copilot {dialect.ToToken()} provider and supports only " +
            (dialect == ApiDialect.Anthropic ? "POST /v1/messages" : "POST /responses (inbound /responses or /v1/responses)")
            + "; Copilot Chat/Completions is not supported by this proxy.",
            statusCode: 400);
    }

    // Writes the Copilot client metadata headers (aligned with opencode's Copilot provider) after dropping
    // caller-relayed values of the same names, so a caller cannot contradict the managed connection identity.
    private static void ApplyCopilotHeaders(HttpRequestMessage request, ApiDialect dialect, GithubCopilotRequestHints hints)
    {
        SetHeader(request, GithubCopilotTransportPolicy.ApiVersionHeader, GithubCopilotTransportPolicy.ApiVersionValue);
        request.Headers.Remove(GithubCopilotTransportPolicy.IntegrationIdHeader);
        SetHeader(request, GithubCopilotTransportPolicy.OpenAiIntentHeader, GithubCopilotTransportPolicy.OpenAiIntentValue);
        SetHeader(
            request,
            GithubCopilotTransportPolicy.InitiatorHeader,
            hints.AgentInitiated ? GithubCopilotTransportPolicy.InitiatorAgent : GithubCopilotTransportPolicy.InitiatorUser);
        SetHeader(request, GithubCopilotTransportPolicy.InteractionTypeHeader, hints.InteractionType);

        request.Headers.Remove(GithubCopilotTransportPolicy.InteractionIdHeader);
        if (hints.InteractionId is { } interactionId)
        {
            request.Headers.TryAddWithoutValidation(GithubCopilotTransportPolicy.InteractionIdHeader, interactionId);
        }

        request.Headers.Remove(GithubCopilotTransportPolicy.VisionRequestHeader);
        if (hints.Vision)
        {
            request.Headers.TryAddWithoutValidation(GithubCopilotTransportPolicy.VisionRequestHeader, "true");
        }

        if (dialect == ApiDialect.Anthropic)
        {
            EnsureAnthropicBeta(request, GithubCopilotTransportPolicy.InterleavedThinkingBeta);
        }

        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(GithubCopilotTransportPolicy.UserAgentValue);
    }

    // Adds a beta flag while keeping the caller's own: opencode replaces the header outright, but Claude Code
    // sends body fields (e.g. context_management) whose beta must survive or the upstream rejects the body.
    private static void EnsureAnthropicBeta(HttpRequestMessage request, string beta)
    {
        string name = GithubCopilotTransportPolicy.AnthropicBetaHeader;
        List<string> flags = request.Headers.TryGetValues(name, out IEnumerable<string>? values)
            ? values.SelectMany(static value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList()
            : [];

        if (flags.Contains(beta, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        flags.Add(beta);
        SetHeader(request, name, string.Join(',', flags));
    }

    private static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
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
