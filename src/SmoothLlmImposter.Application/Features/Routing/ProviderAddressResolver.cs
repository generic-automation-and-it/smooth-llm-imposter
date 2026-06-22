using FluentValidation;
using FluentValidation.Results;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

internal static class ProviderAddressResolver
{
    public static ProviderRoute Resolve(IProviderCatalog catalog, ApiDialect dialect, string? providerName, string propertyName)
    {
        IReadOnlyList<ProviderRoute> providers = catalog.ProvidersFor(dialect);
        ProviderRoute? provider = string.IsNullOrWhiteSpace(providerName)
            ? providers.FirstOrDefault(static p => p.IsDefault)
            : providers.FirstOrDefault(p => string.Equals(p.CredentialProviderName, providerName.Trim(), StringComparison.OrdinalIgnoreCase));

        if (provider is not null)
        {
            return provider;
        }

        string message = string.IsNullOrWhiteSpace(providerName)
            ? $"Dialect '{dialect.ToToken()}' has no enabled default provider."
            : $"Provider '{providerName}' is not an enabled {dialect.ToToken()} provider.";

        throw new ValidationException([new ValidationFailure(propertyName, message)]);
    }
}
