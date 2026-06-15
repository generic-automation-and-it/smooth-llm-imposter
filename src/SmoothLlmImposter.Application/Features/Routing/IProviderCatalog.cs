using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Read model over the configured providers, grouped by dialect and preserving configuration order
/// (first match wins). Built once from <see cref="ImposterOptions"/>.
/// </summary>
public interface IProviderCatalog
{
    /// <summary>Providers of the given dialect, in configuration order.</summary>
    IReadOnlyList<ProviderRoute> ProvidersFor(ApiDialect dialect);
}
