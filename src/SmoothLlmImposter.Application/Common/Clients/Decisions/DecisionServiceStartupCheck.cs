using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

/// <summary>
/// Fails startup on an invalid transport profile of any opted-in decision section (HLD 015 LADR-02, LADR-05).
/// A hosted service rather than <c>ValidateOnStart</c>: the profiles are resolved through the routing options,
/// so a startup validator would report an invalid routing configuration a second time; hosted services start
/// only after options validation has passed.
/// </summary>
internal sealed class DecisionServiceStartupCheck(
    IEnumerable<IDecisionServiceConsumer> consumers,
    IOptionsMonitor<DecisionServiceOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (IDecisionServiceConsumer consumer in consumers.Where(static consumer => consumer.IsOptedIn))
        {
            _ = options.Get(consumer.Section);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
