using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Application.Features.Routing;

internal sealed class ProviderRegistryStartupSeeder(
    IOptions<ImposterOptions> options,
    IProviderRegistry registry,
    ILogger<ProviderRegistryStartupSeeder> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Seed(options.Value.Providers);
        logger.LogInformation("Runtime provider registry seeded with {ProviderCount} providers.", options.Value.Providers.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
