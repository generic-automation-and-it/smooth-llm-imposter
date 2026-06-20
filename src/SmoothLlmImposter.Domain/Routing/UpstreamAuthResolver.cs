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
}
