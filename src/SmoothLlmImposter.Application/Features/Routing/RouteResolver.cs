using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// First-match-wins resolution: scans the dialect's providers in configuration order and returns the
/// first model mapping whose <c>From</c> matches. When nothing matches, falls back to the dialect's
/// default provider (real-provider passthrough), leaving the model unchanged and caching off.
/// </summary>
internal sealed class RouteResolver(IProviderCatalog catalog) : IRouteResolver
{
    public RouteDecision Resolve(ApiDialect dialect, string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new RoutingException("Request is missing a 'model'.");
        }

        // ProvidersFor already excludes disabled providers (single source of truth — see ProviderCatalog).
        IReadOnlyList<ProviderRoute> providers = catalog.ProvidersFor(dialect);

        foreach (ProviderRoute provider in providers)
        {
            foreach (ModelMapping mapping in provider.Models)
            {
                if (ModelMatcher.Matches(mapping.From, model))
                {
                    return new RouteDecision(provider, mapping.To, mapping.Caching, IsImposter: true);
                }
            }
        }

        ProviderRoute? defaultProvider = providers.FirstOrDefault(static p => p.IsDefault);
        if (defaultProvider is null)
        {
            throw new RoutingException(
                $"No imposter route matched model '{model}' and no default {dialect} provider is configured.",
                statusCode: 404);
        }

        return new RouteDecision(defaultProvider, model, CachingEnabled: false, IsImposter: false);
    }

    public RouteDecision ResolveDefault(ApiDialect dialect)
    {
        ProviderRoute? defaultProvider = catalog.ProvidersFor(dialect).FirstOrDefault(static p => p.IsDefault);
        if (defaultProvider is null)
        {
            throw new RoutingException(
                $"No default {dialect} provider is configured to serve a body-less request.",
                statusCode: 404);
        }

        // No inbound model on a body-less request — passthrough to the default upstream unchanged.
        return new RouteDecision(defaultProvider, TargetModel: string.Empty, CachingEnabled: false, IsImposter: false);
    }
}
