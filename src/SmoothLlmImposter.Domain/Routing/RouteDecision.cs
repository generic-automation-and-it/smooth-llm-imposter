namespace SmoothLlmImposter.Domain.Routing;

/// <summary>
/// The outcome of resolving an inbound (dialect, model) pair: which provider to send to, what the
/// model should be rewritten to, and whether caching injection applies. <see cref="IsImposter"/> is
/// <c>true</c> when a mapping matched, <c>false</c> for default passthrough.
/// </summary>
public sealed record RouteDecision(
    ProviderRoute Provider,
    string TargetModel,
    bool CachingEnabled,
    bool IsImposter);
