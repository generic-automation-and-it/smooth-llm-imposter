namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Auth and session-identity header names whose value is masked in the Debug request dump so real keys,
/// session tokens, and account/organization identifiers never reach the log sink in the clear. The
/// Debug sink may still log them (operators should not enable Debug in production). Shared single
/// source of truth for the masked header set so it cannot drift between callers.
/// </summary>
public static class SensitiveHeaderNames
{
    public static readonly IReadOnlySet<string> Values = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "x-api-key", "session_id", "x-opencode-session",
        "x-session-id", "conversation_id",
        "chatgpt-account-id", "openai-organization", "openai-project",
    };
}
