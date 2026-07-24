using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.AuthorizationOverride;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

internal sealed class ImposterRouter : IImposterRouter
{
    private readonly IRouteResolver _resolver;
    private readonly ICredentialStore _credentialStore;
    private readonly ISecretProtector _secretProtector;
    private readonly IAuthorizationOverrideSwitch _overrideSwitch;
    private readonly IReadOnlyDictionary<ApiDialect, IRequestTransformer> _transformers;
    private readonly ILogger<ImposterRouter> _logger;

    public ImposterRouter(
        IRouteResolver resolver,
        ICredentialStore credentialStore,
        ISecretProtector secretProtector,
        IAuthorizationOverrideSwitch overrideSwitch,
        IEnumerable<IRequestTransformer> transformers,
        ILogger<ImposterRouter> logger)
    {
        _resolver = resolver;
        _credentialStore = credentialStore;
        _secretProtector = secretProtector;
        _overrideSwitch = overrideSwitch;
        _transformers = transformers.ToDictionary(t => t.Dialect);
        _logger = logger;
    }

    public async Task<RoutePlan> PlanAsync(
        ApiDialect dialect,
        string requestBody,
        CallerHeaders callerHeaders,
        CancellationToken cancellationToken)
    {
        string model;
        RouteDecision decision;
        IRequestTransformer transformer;
        SessionIdentity sessionIdentity;

        // Parse the request body once and share the parsed object between model extraction and the
        // session resolver. The transformer re-parses (it materializes a mutable JsonNode graph), so an
        // opted-in route now parses twice instead of three times; a passthrough route still parses twice.
        using (JsonDocument document = ParseRequestObject(requestBody))
        {
            JsonElement root = document.RootElement;
            model = ExtractModel(root);
            decision = _resolver.Resolve(dialect, model);

            if (!_transformers.TryGetValue(dialect, out transformer!))
            {
                throw new RoutingException($"No request transformer registered for dialect '{dialect}'.", statusCode: 500);
            }

            // Resolve once per request; only stamp on matched imposter routes to an opted-in provider.
            // Passthrough stays byte-transparent (session=none in the log, no header/body write).
            sessionIdentity = SessionForwardingPolicy.IsOptedIn(decision)
                ? SessionIdentityResolver.Resolve(callerHeaders, root)
                : SessionIdentity.None;
        }

        string transformedBody = transformer.Transform(requestBody, decision, model, sessionIdentity);
        RouteCredentialOverride? credentialOverride = decision.IsImposter
            ? null
            : await ResolvePassthroughCredentialAsync(dialect, decision.Provider.CredentialProviderName, cancellationToken);

        _logger.LogInformation(
            "Routed {Dialect} model '{InboundModel}' -> provider '{Provider}' as '{TargetModel}' (imposter={IsImposter}, caching={Caching}, storedCredential={StoredCredential}, auth={Auth}, session={Session})",
            dialect,
            model,
            decision.Provider.Name,
            decision.TargetModel,
            decision.IsImposter,
            decision.CachingEnabled,
            credentialOverride is not null,
            DescribeAuth(decision, dialect, credentialOverride),
            sessionIdentity.LogToken);

        return new RoutePlan(decision, model, transformedBody, sessionIdentity, credentialOverride);
    }

    public async Task<RoutePlan> PlanPassthroughAsync(
        ApiDialect dialect,
        CallerHeaders callerHeaders,
        CancellationToken cancellationToken)
    {
        // No body, no model (e.g. GET /v1/models): passthrough to the dialect default with no transform.
        // The body forwarded upstream is empty, so the forwarder issues the request with no content.
        // callerHeaders is accepted for interface uniformity; session forwarding never stamps passthrough.
        _ = callerHeaders;
        RouteDecision decision = _resolver.ResolveDefault(dialect);
        RouteCredentialOverride? credentialOverride = await ResolvePassthroughCredentialAsync(dialect, decision.Provider.CredentialProviderName, cancellationToken);

        _logger.LogInformation(
            "Routed {Dialect} body-less request -> provider '{Provider}' (passthrough, no model, storedCredential={StoredCredential}, auth={Auth}, session={Session})",
            dialect,
            decision.Provider.Name,
            credentialOverride is not null,
            DescribeAuth(decision, dialect, credentialOverride),
            SessionIdentity.None.LogToken);

        return new RoutePlan(decision, InboundModel: string.Empty, TransformedBody: string.Empty, SessionIdentity: SessionIdentity.None, credentialOverride);
    }

    private async Task<RouteCredentialOverride?> ResolvePassthroughCredentialAsync(ApiDialect dialect, string providerName, CancellationToken cancellationToken)
    {
        // The authorization override switch is consulted ONLY here, on the passthrough branch. A matched
        // imposter route never enters this method, so it never reads the switch or the store (LADR-003).
        bool forceBearer = _overrideSwitch.IsEnabled(dialect, providerName);

        ProviderCredential? credential = await _credentialStore.GetActiveAsync(dialect, providerName, cancellationToken);
        if (credential is null)
        {
            // Override ON + no active credential ⇒ fail closed (403), never fall back to x-api-key/config key (LADR-005).
            return forceBearer
                ? throw new RoutingException(
                    $"The {dialect}/{providerName} passthrough authorization override is enabled but no active stored credential is configured.",
                    statusCode: 403)
                : null;
        }

        Uri? baseUrlOverride = null;
        if (!string.IsNullOrWhiteSpace(credential.BaseUrlOverride) &&
            !Uri.TryCreate(credential.BaseUrlOverride, UriKind.Absolute, out baseUrlOverride))
        {
            throw new RoutingException($"Stored credential '{credential.Name}' has an invalid base URL override.", statusCode: 500);
        }

        return new RouteCredentialOverride(
            _secretProtector.Unprotect(credential.SecretCiphertext),
            credential.AuthScheme,
            baseUrlOverride,
            credential is AnthropicCredential anthropic ? anthropic.AnthropicVersion : null,
            ForceBearer: forceBearer);
    }

    // Mirrors UpstreamForwarder.ApplyAuthentication via the shared resolver so the log reports the auth the
    // forwarder will actually apply: a resolved Bearer/ApiKey scheme when a secret is present, "none" for a
    // matched imposter route with no configured secret (no header is sent at all), or "caller-passthrough"
    // when the caller's own credential is relayed. Never logs the secret value itself.
    private static string DescribeAuth(RouteDecision decision, ApiDialect dialect, RouteCredentialOverride? credentialOverride)
    {
        string? secret = credentialOverride?.Secret ?? decision.Provider.Secret;
        if (!string.IsNullOrEmpty(secret))
        {
            return UpstreamAuthResolver.ResolveScheme(
                dialect,
                decision.Provider.AuthScheme,
                credentialOverride?.AuthScheme,
                credentialOverride?.ForceBearer ?? false).ToString();
        }

        return decision.IsImposter ? "none" : "caller-passthrough";
    }

    // Parses the request body into an owned JsonDocument (caller disposes) and enforces the object shape.
    // The document is reused for both model extraction and session resolution to avoid re-parsing.
    private static JsonDocument ParseRequestObject(string requestBody)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(requestBody);
        }
        catch (JsonException ex)
        {
            throw new RoutingException($"Request body is not valid JSON: {ex.Message}");
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new RoutingException("Request body must be a JSON object.");
        }

        return document;
    }

    private static string ExtractModel(JsonElement root)
    {
        if (!root.TryGetProperty("model", out JsonElement modelElement) ||
            modelElement.ValueKind != JsonValueKind.String)
        {
            throw new RoutingException("Request is missing a string 'model' property.");
        }

        return modelElement.GetString()!;
    }
}
