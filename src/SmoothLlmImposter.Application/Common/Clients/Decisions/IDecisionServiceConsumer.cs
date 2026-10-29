namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

/// <summary>
/// An internal feature that calls the shared decision service. Each consumer owns its own opt-in; no
/// consumer's flag switches the integration for the others (HLD 015 LADR-02).
/// </summary>
public interface IDecisionServiceConsumer
{
    /// <summary>
    /// The decision section this consumer calls through: the name of its resolved transport profile and
    /// the operator section its transport failures are reported under (HLD 015 LADR-05).
    /// </summary>
    string Section { get; }

    bool IsOptedIn { get; }
}
