namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Auth and session-identity header names whose value is masked in the Debug request dump so real keys,
/// session tokens, and account/organization identifiers never reach the log sink in the clear. The
/// Debug sink may still log them (operators should not enable Debug in production). Shared by the
/// Host's inbound dump and Infrastructure's outbound dump so the two cannot drift — a drift-side
/// value would otherwise be relayed in the clear on whichever side stopped masking it.
/// </summary>
internal static class SensitiveHeaderNames
{
    public static readonly IReadOnlySet<string> Values = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "x-api-key", "session_id", "x-opencode-session",
        "chatgpt-account-id", "openai-organization", "openai-project",
    };
}
