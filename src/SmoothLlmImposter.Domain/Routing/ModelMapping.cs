namespace SmoothLlmImposter.Domain.Routing;

/// <summary>
/// A single from→to model rewrite within a provider. When an inbound model matches
/// <see cref="From"/> (exact or trailing-<c>*</c> wildcard), the request is sent to the owning
/// provider with its model rewritten to <see cref="To"/>. <see cref="Caching"/> opts the request
/// into router-injected prompt caching, since the imposter upstream does not add it itself.
/// <para>
/// <see cref="To"/> may contain the literal token <c>{model}</c>, which expands to the full inbound
/// model name — enabling prefix rewrites that keep the caller's version suffix (e.g.
/// <c>To = "anthropic.{model}"</c> turns <c>claude-opus-4-1</c> into <c>anthropic.claude-opus-4-1</c>).
/// When the token is absent, <see cref="To"/> is a literal replacement.
/// </para>
/// </summary>
public sealed record ModelMapping(string From, string To, bool Caching)
{
    /// <summary>
    /// Resolves the upstream model for a matched <paramref name="inboundModel"/> by expanding the
    /// <c>{model}</c> token in <see cref="To"/> to the inbound model name. Returns <see cref="To"/>
    /// unchanged when the token is absent.
    /// </summary>
    public string ResolveTarget(string inboundModel) =>
        To.Replace("{model}", inboundModel, StringComparison.Ordinal);
}
