using SmoothLlmImposter.Domain.Credentials;

namespace SmoothLlmImposter.Domain.Routing;

/// <summary>
/// A configured upstream: one base URL, one secret, one dialect, identified by <see cref="Name"/>.
/// Holds the model mappings that select it. When no mapping matches an inbound model, the
/// dialect's <see cref="IsDefault"/> provider receives the request unchanged (real-provider passthrough).
/// <see cref="AuthScheme"/> is independent of <see cref="Dialect"/>: <c>null</c> means "use the dialect
/// default" (OpenAI → Bearer, Anthropic → ApiKey), resolved by the forwarder.
/// <see cref="AuthHeader"/> overrides only the <b>header name</b> the credential is written into
/// (<c>null</c> = the scheme's default, <c>Authorization</c>/<c>x-api-key</c>); the value format still
/// follows <see cref="AuthScheme"/>. A gateway that wants the credential in a non-standard header — e.g. the
/// MyCompany Gateway's <c>api-key</c> — sets it.
/// <see cref="RequestNormalization"/> opts the provider into a proxy-side request-normalization profile
/// (HLD 004); <see cref="RequestNormalization.None"/> (default) forwards the body unchanged.
/// <see cref="SessionForwarding"/> opts the provider into a proxy-side session-identity stamp
/// (HLD 009); <see cref="SessionForwarding.None"/> (default) leaves session signals untouched.
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
    RequestNormalization RequestNormalization = RequestNormalization.None,
    bool Enabled = true,
    string? ProviderKey = null,
    string? AuthHeader = null,
    SessionForwarding SessionForwarding = SessionForwarding.None)
{
    /// <summary>
    /// The stable identity used to key credentials and the authorization override — the provider's
    /// dictionary key (<see cref="ProviderKey"/>), never the human-facing <see cref="Name"/>. Callers
    /// must set <see cref="ProviderKey"/> (as <c>ProviderCatalog</c> does from the dictionary key) to get
    /// key-as-identity; the <see cref="Name"/> fallback is a defensive backstop for hand-built routes.
    /// </summary>
    public string CredentialProviderName => string.IsNullOrWhiteSpace(ProviderKey) ? Name : ProviderKey;
}
