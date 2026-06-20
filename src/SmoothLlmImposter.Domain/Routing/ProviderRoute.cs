using SmoothLlmImposter.Domain.Credentials;

namespace SmoothLlmImposter.Domain.Routing;

/// <summary>
/// A configured upstream: one base URL, one secret, one dialect, identified by <see cref="Name"/>.
/// Holds the model mappings that select it. When no mapping matches an inbound model, the
/// dialect's <see cref="IsDefault"/> provider receives the request unchanged (real-provider passthrough).
/// <see cref="AuthScheme"/> is independent of <see cref="Dialect"/>: <c>null</c> means "use the dialect
/// default" (OpenAI → Bearer, Anthropic → ApiKey), resolved by the forwarder.
/// <see cref="RequestNormalization"/> opts the provider into a proxy-side request-normalization profile
/// (HLD 004); <see cref="RequestNormalization.None"/> (default) forwards the body unchanged.
/// </summary>
public sealed record ProviderRoute(
    string Name,
    ApiDialect Dialect,
    Uri BaseUrl,
    string? Secret,
    bool IsDefault,
    string? AnthropicVersion,
    IReadOnlyList<ModelMapping> Models,
    OpenAiUpstreamApi OpenAiUpstreamApi = OpenAiUpstreamApi.Responses,
    CredentialAuthScheme? AuthScheme = null,
    RequestNormalization RequestNormalization = RequestNormalization.None);
