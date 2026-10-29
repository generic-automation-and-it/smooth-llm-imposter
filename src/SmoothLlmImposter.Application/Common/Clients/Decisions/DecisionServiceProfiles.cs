using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

/// <summary>
/// Resolves the transport profile of one decision section (HLD 015 LADR-05). Only sections a registered
/// consumer declares exist, so the set of profiles, and of admission gates keyed by them, stays bounded. A
/// section whose consumer is not opted in resolves to the built-in defaults: its settings are validated only
/// while it is on, so unvalidated values never reach the transport.
/// </summary>
public sealed class DecisionServiceProfiles(
    IEnumerable<IDecisionServiceConsumer> consumers,
    IOptionsMonitor<DecisionServiceOptions> options)
{
    private readonly IDecisionServiceConsumer[] _consumers = consumers.ToArray();

    public DecisionServiceOptions Resolve(string section)
    {
        IDecisionServiceConsumer consumer = _consumers.FirstOrDefault(candidate =>
                string.Equals(candidate.Section, section, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No decision consumer is registered for section '{section}'.");

        return consumer.IsOptedIn ? options.Get(section) : new DecisionServiceOptions();
    }
}
