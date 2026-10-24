using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

/// <summary>
/// Fails startup on an invalid transport profile while a consumer requires the integration (HLD 015 LADR-02).
/// A hosted service rather than <c>ValidateOnStart</c>: the profile is resolved through the routing options,
/// so a startup validator would report an invalid routing configuration a second time; hosted services start
/// only after options validation has passed.
/// </summary>
internal sealed class DecisionServiceStartupCheck(IOptions<DecisionServiceOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = options.Value;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
