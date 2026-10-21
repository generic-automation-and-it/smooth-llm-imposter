namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Whether a connection's credentials may be presented upstream. <see cref="ReauthenticationRequired"/> is
/// sticky until a fresh login replaces the revision: an expired, revoked or refresh-failed connection fails
/// closed instead of borrowing caller auth or an older token pair (R9).
/// </summary>
public enum GithubCopilotConnectionState
{
    Active,
    ReauthenticationRequired
}

/// <summary>
/// One <b>published</b> connection revision — the immutable in-memory snapshot routing reads. It is the only
/// place a plaintext token exists outside a login/refresh call, and both protocol profiles of a prefix resolve
/// their token and API origin from the same revision, so auth and endpoint can never mix accounts (R4/D2).
/// </summary>
public sealed record GithubCopilotPublishedConnection(
    string Prefix,
    string ConnectionId,
    string ClientId,
    string ApiOrigin,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAtUtc,
    long Revision,
    GithubCopilotConnectionState State)
{
    public bool HasUsableCredentialAt(DateTimeOffset now) =>
        State == GithubCopilotConnectionState.Active &&
        (ExpiresAtUtc is null || ExpiresAtUtc > now);
}

/// <summary>
/// In-memory published-connection registry. Hydrated from durable rows at startup and replaced after a
/// successful login/refresh or a revision-scoped invalidation; never read from SQLite on the inference path
/// (R4).
/// </summary>
public interface IGithubCopilotConnectionRegistry
{
    /// <summary>Replaces the published revision for <paramref name="connection"/>'s prefix.</summary>
    void Publish(GithubCopilotPublishedConnection connection);

    GithubCopilotPublishedConnection? GetByPrefix(string prefix);

    GithubCopilotPublishedConnection[] Snapshot();

    /// <summary>
    /// Marks a connection unusable only if <paramref name="revision"/> is still the published revision, so a
    /// late upstream 401 cannot invalidate a newer login that has already replaced it. The durable row is
    /// retained (D3) — routing simply fails closed.
    /// </summary>
    bool InvalidateIfCurrent(string prefix, long revision);

}
