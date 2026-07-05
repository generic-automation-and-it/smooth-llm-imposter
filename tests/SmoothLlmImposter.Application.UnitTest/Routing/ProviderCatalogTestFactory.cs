using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

internal static class ProviderCatalogTestFactory
{
    public static ProviderCatalog SeededCatalog(params ProviderOptions[] providers) =>
        SeededCatalog(providers.ToDictionary(static p => p.Name!, StringComparer.Ordinal));

    public static ProviderCatalog SeededCatalog(IReadOnlyDictionary<string, ProviderOptions> providers)
    {
        var registry = new InMemoryProviderRegistry();
        registry.Seed(providers);

        return new ProviderCatalog(registry, Options.Create(new ImposterOptions()));
    }

    public static ProviderCatalog UnseededCatalog(ImposterOptions fallbackOptions) =>
        new(new InMemoryProviderRegistry(), Options.Create(fallbackOptions));
}
