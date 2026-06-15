namespace SmoothLlmImposter.Domain.Routing;

/// <summary>
/// A single from→to model rewrite within a provider. When an inbound model matches
/// <see cref="From"/> (exact or trailing-<c>*</c> wildcard), the request is sent to the owning
/// provider with its model rewritten to <see cref="To"/>. <see cref="Caching"/> opts the request
/// into router-injected prompt caching, since the imposter upstream does not add it itself.
/// </summary>
public sealed record ModelMapping(string From, string To, bool Caching);
