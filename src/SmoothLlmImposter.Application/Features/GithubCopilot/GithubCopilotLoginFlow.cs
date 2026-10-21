namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Lifecycle of one in-memory device login. A flow is bounded by its device-code expiry and is dropped on
/// any terminal outcome; a process restart loses it, so an unfinished login must be started again (R3).
/// </summary>
public enum GithubCopilotLoginState
{
    /// <summary>A device code was issued and the user has not approved yet.</summary>
    Pending,

    /// <summary>The token endpoint issued a pair and identity/entitlement validation succeeded.</summary>
    TokenReceived,

    /// <summary>The credential row is durably saved but its revision is not published (D2 window).</summary>
    Saved,

    /// <summary>The saved revision is published to both provider definitions and the routing snapshot.</summary>
    Published,

    /// <summary>A terminal error (denial, expiry, entitlement, invalid origin). The flow is discarded.</summary>
    Failed
}

/// <summary>
/// One pending login's server-side state. Token and device-code fields are process-memory secrets: they are
/// never serialized into a response or a log line, and the device code never leaves this object.
/// </summary>
internal sealed class GithubCopilotLoginFlow
{
    public required string LoginId { get; init; }
    public required string Prefix { get; init; }
    public required string ClientId { get; init; }
    public required string DeviceCode { get; set; }
    public required string UserCode { get; init; }
    public required string VerificationUri { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public int IntervalSeconds { get; set; }
    public DateTimeOffset NextPollAtUtc { get; set; }
    public GithubCopilotLoginState State { get; set; } = GithubCopilotLoginState.Pending;

    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset? AccessTokenExpiresAtUtc { get; set; }

    public string? GitHubUser { get; set; }
    public string? ApiOrigin { get; set; }

    public string? ConnectionId { get; set; }
    public long? Revision { get; set; }

    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }

    public int RetryAfterSeconds(DateTimeOffset now) =>
        NextPollAtUtc <= now
            ? 0
            : Math.Max(1, (int)Math.Ceiling((NextPollAtUtc - now).TotalSeconds));
}
