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

    public async Task<RoutePlan> PlanAsync(ApiDialect dialect, string requestBody, CancellationToken cancellationToken)
    {
        string model = ExtractModel(requestBody);
        RouteDecision decision = _resolver.Resolve(dialect, model);

        if (!_transformers.TryGetValue(dialect, out IRequestTransformer? transformer))
        {
            throw new RoutingException($"No request transformer registered for dialect '{dialect}'.", statusCode: 500);
        }

        string transformedBody = transformer.Transform(requestBody, decision, model);
        RouteCredentialOverride? credentialOverride = decision.IsImposter
            ? null
            : await ResolvePassthroughCredentialAsync(dialect, cancellationToken);

        _logger.LogInformation(
            "Routed {Dialect} model '{InboundModel}' -> provider '{Provider}' as '{TargetModel}' (imposter={IsImposter}, caching={Caching}, storedCredential={StoredCredential})",
            dialect,
            model,
            decision.Provider.Name,
            decision.TargetModel,
            decision.IsImposter,
            decision.CachingEnabled,
            credentialOverride is not null);

        return new RoutePlan(decision, model, transformedBody, credentialOverride);
    }

    public async Task<RoutePlan> PlanPassthroughAsync(ApiDialect dialect, CancellationToken cancellationToken)
    {
        // No body, no model (e.g. GET /v1/models): passthrough to the dialect default with no transform.
        // The body forwarded upstream is empty, so the forwarder issues the request with no content.
        RouteDecision decision = _resolver.ResolveDefault(dialect);
        RouteCredentialOverride? credentialOverride = await ResolvePassthroughCredentialAsync(dialect, cancellationToken);

        _logger.LogInformation(
            "Routed {Dialect} body-less request -> provider '{Provider}' (passthrough, no model, storedCredential={StoredCredential})",
            dialect,
            decision.Provider.Name,
            credentialOverride is not null);

        return new RoutePlan(decision, InboundModel: string.Empty, TransformedBody: string.Empty, credentialOverride);
    }

    private async Task<RouteCredentialOverride?> ResolvePassthroughCredentialAsync(ApiDialect dialect, CancellationToken cancellationToken)
    {
        // The authorization override switch is consulted ONLY here, on the passthrough branch. A matched
        // imposter route never enters this method, so it never reads the switch or the store (LADR-003).
        bool forceBearer = _overrideSwitch.IsEnabled(dialect);

        ProviderCredential? credential = await _credentialStore.GetActiveAsync(dialect, cancellationToken);
        if (credential is null)
        {
            // Override ON + no active credential ⇒ fail closed (403), never fall back to x-api-key/config key (LADR-005).
            return forceBearer
                ? throw new RoutingException(
                    $"The {dialect} passthrough authorization override is enabled but no active stored credential is configured.",
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

    private static string ExtractModel(string requestBody)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(requestBody);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new RoutingException("Request body must be a JSON object.");
            }

            if (!document.RootElement.TryGetProperty("model", out JsonElement modelElement) ||
                modelElement.ValueKind != JsonValueKind.String)
            {
                throw new RoutingException("Request is missing a string 'model' property.");
            }

            return modelElement.GetString()!;
        }
        catch (JsonException ex)
        {
            throw new RoutingException($"Request body is not valid JSON: {ex.Message}");
        }
    }
}
