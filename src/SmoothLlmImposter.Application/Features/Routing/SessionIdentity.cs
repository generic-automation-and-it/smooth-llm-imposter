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

    /// <summary>Safe log surface: <c>captured</c>, <c>derived</c>, or <c>none</c> — never the raw value.
    /// Cached at construction because <see cref="Source"/> is fixed for the lifetime of a record instance.
    /// Routed through the static <see cref="LogTokenFor(SessionIdentitySource)"/> helper so the dependency
    /// on <see cref="Source"/> is explicit and not implicit on positional-record parameter initialization
    /// order; the throw branch is reachable only if a future enum value is added without updating the
    /// switch.</summary>
    public string LogToken { get; } = LogTokenFor(Source);

    private static string LogTokenFor(SessionIdentitySource source) => source switch
    {
        SessionIdentitySource.None => "none",
        SessionIdentitySource.Captured => "captured",
        SessionIdentitySource.Derived => "derived",
        _ => throw new UnreachableException($"Unknown SessionIdentitySource value: {source}."),
    };
}

/// <summary>How the session identity was obtained for this request.</summary>
public enum SessionIdentitySource
{
    None = 0,
    Captured = 1,
    Derived = 2
}
