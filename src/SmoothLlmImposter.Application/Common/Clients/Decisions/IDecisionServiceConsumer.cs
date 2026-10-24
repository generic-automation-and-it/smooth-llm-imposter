namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

/// <summary>
/// An internal feature that calls the shared decision service. Each consumer owns its own opt-in; no
/// consumer's flag switches the integration for the others (HLD 015 LADR-02).
/// </summary>
public interface IDecisionServiceConsumer
{
    bool IsOptedIn { get; }
}
