using Microsoft.Extensions.Options;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Materialises <see cref="ImposterOptions"/> into immutable <see cref="ProviderRoute"/> domain objects,
/// indexed by dialect. Validation of the options happens at startup (see ImposterOptionsValidator);
/// this type assumes already-valid options.
/// </summary>
internal sealed class ProviderCatalog : IProviderCatalog
{
    private readonly Dictionary<ApiDialect, List<ProviderRoute>> _byDialect = new();

    public ProviderCatalog(IOptions<ImposterOptions> options)
    {
        foreach (ProviderOptions provider in options.Value.Providers)
        {
            ApiDialect dialect = ApiDialectParser.Parse(provider.Api);

            var route = new ProviderRoute(
                provider.Name,
                dialect,
                new Uri(provider.BaseUrl, UriKind.Absolute),
                provider.ApiKey,
                provider.IsDefault,
                provider.AnthropicVersion,
                provider.Models
                    .Select(m => new ModelMapping(m.From, m.To, m.Caching))
                    .ToArray());

            if (!_byDialect.TryGetValue(dialect, out List<ProviderRoute>? routes))
            {
                routes = [];
                _byDialect[dialect] = routes;
            }

            routes.Add(route);
        }
    }

    public IReadOnlyList<ProviderRoute> ProvidersFor(ApiDialect dialect) =>
        _byDialect.TryGetValue(dialect, out List<ProviderRoute>? routes) ? routes : [];
}
