using SmoothLlmImposter.Domain.Credentials;

namespace SmoothLlmImposter.Domain.Routing;

/// <summary>
/// Single source of truth for how an upstream is authenticated when a secret is present. Shared by the
/// forwarder (which writes the header) and the router (which logs the effective scheme) so the two cannot
/// drift. The wire <c>scheme</c> is decoupled from <see cref="ApiDialect"/>: precedence is
/// <c>credential ?? provider ?? dialect default</c>, with the authorization override (HLD 003) forcing
/// <see cref="CredentialAuthScheme.Bearer"/> regardless.
/// </summary>
public static class UpstreamAuthResolver
{
    /// <summary>The scheme applied to a non-empty secret.</summary>
    public static CredentialAuthScheme ResolveScheme(
        ApiDialect dialect,
        CredentialAuthScheme? providerScheme,
        CredentialAuthScheme? credentialScheme,
        bool forceBearer)
        => forceBearer
            ? CredentialAuthScheme.Bearer
            : credentialScheme ?? providerScheme ?? DefaultSchemeFor(dialect);

    /// <summary>Dialect default when no scheme is configured: OpenAI → Bearer, Anthropic → x-api-key.</summary>
    public static CredentialAuthScheme DefaultSchemeFor(ApiDialect dialect) => dialect switch
    {
        ApiDialect.Anthropic => CredentialAuthScheme.ApiKey,
        _ => CredentialAuthScheme.Bearer,
    };

    /// <summary>
    /// The conventional header a scheme writes its credential into when a provider does not override it via
    /// <see cref="ProviderRoute.AuthHeader"/>: <see cref="CredentialAuthScheme.Bearer"/> → <c>Authorization</c>,
    /// <see cref="CredentialAuthScheme.ApiKey"/> → <c>x-api-key</c>. A provider whose gateway expects the
    /// credential in a differently-named header (e.g. the LEGO codex gateway's <c>api-key</c>) sets
    /// <c>AuthHeader</c> to relocate the value; the value format still follows the scheme (Bearer keeps its
    /// <c>Bearer </c> prefix, ApiKey stays the raw token).
    /// </summary>
    public static string DefaultHeaderNameFor(CredentialAuthScheme scheme) => scheme switch
    {
        CredentialAuthScheme.Bearer => "Authorization",
        _ => "x-api-key",
    };
}
