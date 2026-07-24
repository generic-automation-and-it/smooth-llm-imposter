namespace SmoothLlmImposter.Domain.Routing;

/// <summary>
/// Selects a proxy-side session-identity forwarding profile for a provider (HLD 009). When set, a matched
/// imposter route stamps a resolved session identity onto the outbound request so an upstream that groups
/// traffic by session (e.g. opencode-go diag) can attribute Codex/Claude Code calls. Off by default —
/// <see cref="None"/> keeps the request byte-transparent for this concern.
/// </summary>
public enum SessionForwarding
{
    /// <summary>No session stamping — request is unchanged for this concern (default, safe).</summary>
    None = 0,

    /// <summary>
    /// Stamp opencode-go session signals on a matched imposter route: <c>session_id</c> JSON body field
    /// (OpenAI dialect) and <c>x-opencode-session</c> header (both dialects). Identity is resolved
    /// per-request only (capture → derive → none); never stored (NFR-001).
    /// </summary>
    OpencodeGo = 1
}

/// <summary>
/// Parses the operator-facing <c>SessionForwarding</c> config string into <see cref="SessionForwarding"/>.
/// Empty/whitespace/null defaults to <see cref="SessionForwarding.None"/> (safe default).
/// </summary>
public static class SessionForwardingParser
{
    /// <summary>
    /// Returns <c>true</c> for any accepted spelling, including the safe default (null/empty/whitespace).
    /// A blank input is intentionally reported as "valid + None" rather than "invalid" so an omitted
    /// config field is not flagged at startup; only a present-but-unrecognised value returns <c>false</c>.
    /// </summary>
    public static bool TryParse(string? value, out SessionForwarding forwarding)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            forwarding = SessionForwarding.None;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "none":
                forwarding = SessionForwarding.None;
                return true;
            case "opencode-go":
            case "opencode_go":
            case "opencodego":
                forwarding = SessionForwarding.OpencodeGo;
                return true;
            default:
                forwarding = SessionForwarding.None;
                return false;
        }
    }

    public static SessionForwarding Parse(string? value) =>
        TryParse(value, out SessionForwarding forwarding)
            ? forwarding
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown session forwarding.");
}

/// <summary>The single encoding of the session-forwarding opt-in gate (HLD 009 / LADR-01).</summary>
public static class SessionForwardingPolicy
{
    /// <summary>
    /// True when the route is a matched imposter to a provider whose
    /// <see cref="ProviderRoute.SessionForwarding"/> profile is <see cref="SessionForwarding.OpencodeGo"/>.
    /// Router (resolve gate) and transformers (stamp gate) consult this — never re-encode the predicate.
    /// </summary>
    public static bool IsOptedIn(RouteDecision decision) =>
        decision.IsImposter && decision.Provider.SessionForwarding == SessionForwarding.OpencodeGo;
}
