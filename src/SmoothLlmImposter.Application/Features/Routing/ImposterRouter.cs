using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

internal sealed class ImposterRouter : IImposterRouter
{
    private readonly IRouteResolver _resolver;
    private readonly ICredentialStore _credentialStore;
    private readonly ISecretProtector _secretProtector;
    private readonly IReadOnlyDictionary<ApiDialect, IRequestTransformer> _transformers;
    private readonly ILogger<ImposterRouter> _logger;

    public ImposterRouter(
        IRouteResolver resolver,
        ICredentialStore credentialStore,
        ISecretProtector secretProtector,
        IEnumerable<IRequestTransformer> transformers,
        ILogger<ImposterRouter> logger)
    {
        _resolver = resolver;
        _credentialStore = credentialStore;
        _secretProtector = secretProtector;
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

    private async Task<RouteCredentialOverride?> ResolvePassthroughCredentialAsync(ApiDialect dialect, CancellationToken cancellationToken)
    {
        ProviderCredential? credential = await _credentialStore.GetActiveAsync(dialect, cancellationToken);
        if (credential is null)
        {
            return null;
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
            credential is AnthropicCredential anthropic ? anthropic.AnthropicVersion : null);
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
