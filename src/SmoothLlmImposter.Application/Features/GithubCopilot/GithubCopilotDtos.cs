using System.Text.Json.Serialization;
using SmoothLlmImposter.Domain.GithubCopilot;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Start Login result (step 01). The GitHub device code never appears here; the user code and verification
/// URI are the operator-facing half of the grant, and <see cref="Message"/> states the next step for a caller
/// driving the workflow with curl (R3).
/// </summary>
public sealed record GithubCopilotLoginStartResponse(
    string LoginId,
    string Prefix,
    string ClientId,
    string[] ProviderKeys,
    string VerificationUri,
    string UserCode,
    DateTimeOffset ExpiresAtUtc,
    int IntervalSeconds,
    string CompleteUrl,
    string Message);

/// <summary>Workflow outcome of one Complete call; the Host maps it onto 200/202/404/400/503.</summary>
public enum GithubCopilotCompleteOutcome
{
    Pending,
    Succeeded,
    SavedNotPublished,
    Retryable,
    Failed
}

/// <summary>
/// Complete Login result (step 02). <see cref="SavedRevision"/>/<see cref="PublishedRevision"/> expose the D2
/// saved-versus-published window without leaking a token; <see cref="RetryAfterSeconds"/> is set while pending.
/// </summary>
public sealed record GithubCopilotLoginCompleteResponse(
    string LoginId,
    string Prefix,
    string Outcome,
    string[] ProviderKeys,
    string? ConnectionId,
    long? SavedRevision,
    long? PublishedRevision,
    bool PublicationReady,
    int? RetryAfterSeconds,
    string Message,
    string? ErrorCode = null)
{
    [JsonIgnore]
    public GithubCopilotCompleteOutcome Status =>
        Outcome switch
        {
            "pending" => GithubCopilotCompleteOutcome.Pending,
            "connected" => GithubCopilotCompleteOutcome.Succeeded,
            "saved_not_published" => GithubCopilotCompleteOutcome.SavedNotPublished,
            "retryable" => GithubCopilotCompleteOutcome.Retryable,
            _ => GithubCopilotCompleteOutcome.Failed
        };
}

/// <summary>Per-provider readiness. <see cref="RoutingReady"/> needs Enabled, usable credentials and a mapping.</summary>
public sealed record GithubCopilotProviderStatus(string Key, bool Enabled, bool RoutingReady);

/// <summary>
/// Safe durable status for one connection (steps 03/05). It distinguishes a saved-but-unpublished revision
/// (restart hint) from a routing gap caused by a missing mapping or a disabled provider (configuration hint).
/// </summary>
public sealed record GithubCopilotConnectionStatus(
    string ConnectionId,
    string Prefix,
    string GitHubUser,
    string ClientId,
    string ApiOrigin,
    long SavedRevision,
    long? PublishedRevision,
    bool PublicationReady,
    bool ReauthenticationRequired,
    DateTimeOffset? ExpiresAtUtc,
    GithubCopilotProviderStatus[] Providers,
    string Message);

/// <summary>Read-only connection list. Never includes a token, ciphertext or device code.</summary>
public sealed record GithubCopilotConnectionListResponse(
    GithubCopilotConnectionStatus[] Connections,
    string Message);

/// <summary>Account-advertised model. Discovery never creates mappings, enables targets or reconciles state.</summary>
public sealed record GithubCopilotModelResponse(string Id, string[] Endpoints, string? Policy);

/// <summary>Model-discovery result (step 03) with the mapping hint that makes the next step self-service.</summary>
public sealed record GithubCopilotModelsDiscoveryResponse(
    string ConnectionId,
    string[] ProviderKeys,
    GithubCopilotModelResponse[] Models,
    string Message);

/// <summary>
/// Builds the human-readable <c>message</c> field carried by every Copilot admin response. Interpolates only
/// already-returned safe fields (verification URI, user code, IDs, provider keys, Retry-After) — never a token,
/// the GitHub device code, or a raw OAuth body.
/// </summary>
internal static class GithubCopilotMessages
{
    public static string Start(GithubCopilotLoginStartResponse response) =>
        $"Open {response.VerificationUri} in a browser, enter {response.UserCode}, then poll " +
        $"POST {response.CompleteUrl} every {response.IntervalSeconds}s. The login expires at " +
        $"{response.ExpiresAtUtc:u}.";

    public static string Pending(string verificationUri, string userCode, string completeUrl, int retryAfterSeconds) =>
        $"Authorization still pending — open {verificationUri} and enter {userCode} if you have not, " +
        $"then retry POST {completeUrl} after {retryAfterSeconds}s.";

    public static string Connected(string connectionId, string prefix) =>
        $"Connection {connectionId} ({DescribePrefix(prefix)}) saved and published. Configure routing: " +
        $"PUT /admin/providers/{GithubCopilotProfileKey.Key(prefix, ApiDialect.Anthropic)} and " +
        $"PUT /admin/providers/{GithubCopilotProfileKey.Key(prefix, ApiDialect.OpenAi)} each with a " +
        "From/To model mapping.";

    public static string SavedNotPublished(string connectionId, long savedRevision) =>
        $"Credential {connectionId} (revision {savedRevision}) is saved but not yet published — a restart " +
        "re-materializes missing provider pairs without another token exchange. Retry this Complete call first; " +
        "it only republishes and never re-exchanges the consumed device grant.";

    public static string Failed(string code) =>
        code switch
        {
            "access_denied" => "The GitHub user denied the authorization request. Start a new login when ready.",
            "expired_token" => "The device code expired before approval. Start a new login to get a fresh code.",
            "invalid_grant" => "GitHub rejected the device grant. Start a new login; if it repeats, check the OAuth application's device flow setting and client ID.",
            "copilot_not_enabled" => "The GitHub account is not entitled to Copilot (chat_enabled is false). Enable Copilot for that account or sign in with another account.",
            "invalid_api_origin" => "GitHub returned an API origin that is not a GitHub-controlled HTTPS origin; the connection was refused.",
            "login_not_found" => "No pending login matches that login ID — it was completed, expired, or the process restarted. Start a new login.",
            "github_unavailable" => "GitHub could not be reached for this step. The login is still pending — retry the same Complete call; no new device grant is needed.",
            "save_failed" => "The credential could not be written to the local store. The login is retained in memory — retry the same Complete call; GitHub is not contacted again.",
            "publication_failed" => "The credential is saved but its provider pair could not be published. Retry once, or restart the host to re-materialize it; no new token exchange is needed.",
            _ => "The login failed. Start a new login."
        };

    public static string ConnectionStatus(
        string prefix,
        bool publicationReady,
        GithubCopilotProviderStatus[] providers,
        bool reauthenticationRequired)
    {
        if (!publicationReady)
        {
            return $"Connection ({DescribePrefix(prefix)}) is saved but its revision is not published — restart " +
                   "the host to re-materialize the provider pair; no new token exchange is needed.";
        }

        if (reauthenticationRequired)
        {
            return $"Connection ({DescribePrefix(prefix)}) requires reauthorization (expired, revoked or refresh failed). " +
                   "Start a new login; routing fails closed until then.";
        }

        GithubCopilotProviderStatus[] notReady = providers.Where(static provider => !provider.RoutingReady).ToArray();
        if (notReady.Length > 0)
        {
            return $"Connection ({DescribePrefix(prefix)}) is published. Not routing yet for " +
                   $"{string.Join(", ", notReady.Select(static provider => provider.Key))}: add a From/To model " +
                   $"mapping with PUT /admin/providers/<key> and ensure the provider is enabled. A restart is not required.";
        }

        return $"Connection ({DescribePrefix(prefix)}) is published and routing-ready for both protocol providers.";
    }

    public static string List(GithubCopilotConnectionStatus[] connections) =>
        connections.Length == 0
            ? "No GitHub Copilot connections saved. Start one with POST /admin/github-copilot/logins, supplying your OAuth application's clientId."
            : $"{connections.Length} GitHub Copilot connection(s). GET /admin/github-copilot/connections/<connectionId> reports publication and routing readiness; model discovery is GET .../models.";

    public static string Models(string connectionId, GithubCopilotProviderStatus[] providers) =>
        $"Discovered models for connection {connectionId}. Map a model with PUT /admin/providers/" +
        $"{providers.FirstOrDefault()?.Key ?? "<provider-key>"}: {{\"Models\":[{{\"From\":\"<caller-model>\",\"To\":\"<discovered-model>\"}}]}}. " +
        "Discovery does not create mappings or enable providers.";

    private static string DescribePrefix(string prefix) =>
        string.IsNullOrEmpty(prefix) ? "unprefixed pair" : $"prefix {prefix}";
}
