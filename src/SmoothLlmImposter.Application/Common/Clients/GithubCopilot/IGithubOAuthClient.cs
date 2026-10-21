namespace SmoothLlmImposter.Application.Common.Clients.GithubCopilot;

/// <summary>
/// GitHub's device-authorization grant (RFC 8628) as returned by the device-code endpoint. The
/// <see cref="DeviceCode"/> is server-side only: it never enters a response, a log line, or the store.
/// </summary>
public sealed record GithubDeviceCode(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    DateTimeOffset ExpiresAtUtc,
    int IntervalSeconds);

/// <summary>Outcome of one token-endpoint poll or refresh call, classified into the workflow's state machine.</summary>
public enum GithubTokenPollKind
{
    /// <summary>The user has not approved yet; poll again after the interval.</summary>
    Pending,

    /// <summary>GitHub asked the client to slow down; retry after the (raised) interval.</summary>
    SlowDown,

    /// <summary>The user denied the request — terminal.</summary>
    Denied,

    /// <summary>The device code expired — terminal.</summary>
    Expired,

    /// <summary>The request itself was rejected (bad client ID, malformed code) — terminal.</summary>
    Rejected,

    /// <summary>A token pair was issued.</summary>
    Success
}

/// <summary>
/// One token-endpoint result. <see cref="AccessToken"/>/<see cref="RefreshToken"/> are plaintext in process
/// memory only; they are encrypted before they reach the store and never returned or logged.
/// </summary>
public sealed record GithubTokenPollResult(
    GithubTokenPollKind Kind,
    string? AccessToken = null,
    string? RefreshToken = null,
    DateTimeOffset? AccessTokenExpiresAtUtc = null,
    int? IntervalSeconds = null);

/// <summary>The authenticated GitHub account identity used to bind a connection to its account.</summary>
public sealed record GithubUser(string Login, long Id);

/// <summary>
/// Copilot entitlement and the account-discovered API origin. <see cref="ChatEnabled"/> must be explicitly
/// true for a login to succeed; a missing/malformed lookup is not a successful setup.
/// </summary>
public sealed record GithubCopilotEntitlement(bool ChatEnabled, string ApiOrigin);

/// <summary>
/// Device-flow OAuth against github.com. These are uncached credential operations: no automatic POST retry
/// policy is attached (local flow state owns retry decisions) and no redirect is ever followed.
/// </summary>
public interface IGithubOAuthClient
{
    Task<GithubDeviceCode> StartDeviceFlowAsync(string clientId, CancellationToken cancellationToken);

    /// <summary>One due poll of the token endpoint for a pending grant.</summary>
    Task<GithubTokenPollResult> PollTokenAsync(string clientId, string deviceCode, CancellationToken cancellationToken);

    /// <summary>Exchanges a saved refresh token for a replacement pair using the connection's saved client ID.</summary>
    Task<GithubTokenPollResult> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken);

    Task<GithubUser?> GetUserAsync(string accessToken, CancellationToken cancellationToken);

    Task<GithubCopilotEntitlement?> GetCopilotEntitlementAsync(string accessToken, CancellationToken cancellationToken);
}
