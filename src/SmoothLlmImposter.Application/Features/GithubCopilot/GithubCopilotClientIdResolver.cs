namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Resolves the effective OAuth application client ID for a login (R10): explicitly supplied Start Login
/// <c>clientId</c> &gt; the saved client ID of that prefix's connection &gt; the optional startup default.
/// A client ID is an opaque, case-sensitive public identifier, not a secret. A supplied-but-invalid value is
/// rejected rather than silently falling back, and no client secret exists in the device-flow contract.
/// </summary>
public static class GithubCopilotClientIdResolver
{
    public const int MaxClientIdLength = 128;

    public static bool IsValid(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > MaxClientIdLength)
        {
            return false;
        }

        foreach (char c in clientId)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns the effective client ID, or null when none can be resolved. <paramref name="invalidExplicit"/>
    /// distinguishes "not supplied" from "supplied but malformed": the latter must be a 400, never a fallback.
    /// </summary>
    public static string? Resolve(string? explicitClientId, string? savedClientId, string? fallbackClientId, out bool invalidExplicit)
    {
        invalidExplicit = false;

        if (explicitClientId is not null)
        {
            if (!IsValid(explicitClientId))
            {
                invalidExplicit = true;
                return null;
            }

            return explicitClientId;
        }

        if (IsValid(savedClientId))
        {
            return savedClientId;
        }

        return IsValid(fallbackClientId) ? fallbackClientId : null;
    }
}
