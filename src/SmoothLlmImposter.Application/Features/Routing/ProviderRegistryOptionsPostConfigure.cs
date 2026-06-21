using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Makes each options snapshot read from the runtime registry after startup seeding. Before seeding, it
/// leaves the static bound+env options untouched so ValidateOnStart and the seeder see the baseline.
/// </summary>
internal sealed class ProviderRegistryOptionsPostConfigure(IProviderRegistry registry) : IPostConfigureOptions<ImposterOptions>
{
    public void PostConfigure(string? name, ImposterOptions options)
    {
        if (!registry.IsSeeded)
        {
            return;
        }

        // Safe to mutate the init-set dictionary: IOptionsSnapshot constructs a fresh ImposterOptions per
        // scope, so Clear()+refill never leaks back to the singleton or another request's snapshot.
        options.Providers.Clear();
        foreach ((string key, ProviderOptions provider) in registry.Snapshot())
        {
            options.Providers[key] = provider;
        }
    }
}
