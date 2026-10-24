namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

/// <summary>
/// The any-consumer predicate of HLD 015 LADR-02: the integration is required while at least one registered
/// consumer is opted in. Validation and the transport's resilience profile follow it.
/// </summary>
public sealed class DecisionServiceDemand(IEnumerable<IDecisionServiceConsumer> consumers)
{
    public bool IsRequired => consumers.Any(static consumer => consumer.IsOptedIn);
}
