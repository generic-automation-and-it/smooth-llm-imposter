namespace SmoothLlmImposter.Domain.Credentials;

public enum CredentialAuthScheme
{
    ApiKey = 0,
    Bearer = 1
}

/// <summary>
/// Parses the optional <c>AuthScheme</c> config value shared by stored credentials and configured
/// providers. The scheme is decoupled from the wire <c>Dialect</c>: an OpenAI-dialect upstream may
/// authenticate with <c>x-api-key</c> and an Anthropic-dialect upstream with <c>Authorization: Bearer</c>.
/// </summary>
public static class CredentialAuthSchemeParser
{
    /// <summary>
    /// Parses an optional auth-scheme config value. Null/empty yields <c>null</c> (meaning "fall back to
    /// the dialect default"); <c>apikey</c>/<c>bearer</c> (case-insensitive) yield the scheme; any other
    /// value fails so startup validation can reject it.
    /// </summary>
    public static bool TryParse(string? value, out CredentialAuthScheme? scheme)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            scheme = null;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "apikey":
            case "api-key":
            case "api_key":
                scheme = CredentialAuthScheme.ApiKey;
                return true;
            case "bearer":
                scheme = CredentialAuthScheme.Bearer;
                return true;
            default:
                scheme = null;
                return false;
        }
    }
}
