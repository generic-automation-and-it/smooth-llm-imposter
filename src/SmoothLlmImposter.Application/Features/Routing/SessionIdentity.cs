using System.Diagnostics;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Per-request session identity resolved for an opted-in imposter route (HLD 009). The raw
/// <see cref="Value"/> is stamped onto the outbound request and must never be logged; use
/// <see cref="LogToken"/> for the routing Information line.
/// </summary>
public sealed record SessionIdentity(string? Value, SessionIdentitySource Source)
{
    public static SessionIdentity None { get; } = new(null, SessionIdentitySource.None);

    /// <summary>True when <see cref="Value"/> is a non-blank, stampable identity. Uses
    /// <see cref="string.IsNullOrWhiteSpace(string?)"/> so whitespace-only resolved values are
    /// treated as absent (never stamped, never logged as a value).</summary>
    public bool HasValue => !string.IsNullOrWhiteSpace(Value);

    /// <summary>Safe log surface: <c>captured</c>, <c>derived</c>, or <c>none</c> — never the raw value.</summary>
    public string LogToken => Source switch
    {
        SessionIdentitySource.None => "none",
        SessionIdentitySource.Captured => "captured",
        SessionIdentitySource.Derived => "derived",
        _ => throw new UnreachableException($"Unknown SessionIdentitySource value: {Source}."),
    };
}

/// <summary>How the session identity was obtained for this request.</summary>
public enum SessionIdentitySource
{
    None = 0,
    Captured = 1,
    Derived = 2
}
