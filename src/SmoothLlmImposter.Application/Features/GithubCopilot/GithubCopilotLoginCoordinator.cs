using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.GithubCopilot;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Domain.GithubCopilot;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>Result of one login step, already shaped as the admin response DTO.</summary>
public interface IGithubCopilotLoginCoordinator
{
    Task<GithubCopilotLoginStartResponse> StartAsync(string? prefix, string? clientId, CancellationToken cancellationToken);

    Task<GithubCopilotLoginCompleteResponse> CompleteAsync(string loginId, CancellationToken cancellationToken);
}

/// <summary>
/// Owns the bounded, in-memory device-login state machine:
/// pending → token received → identity/entitlement validated → durably saved → published.
/// A consumed device grant is never re-exchanged: once the token endpoint returns a pair it is retained in
/// the flow across retryable validation, save and publication failures. A process restart loses the flow, so
/// unfinished logins must be started again; a published revision is recovered at startup instead (R3/D2).
/// </summary>
internal sealed class GithubCopilotLoginCoordinator(
    IGithubOAuthClient oauthClient,
    IGithubCopilotConnectionStore store,
    IGithubCopilotSecretProtector protector,
    IGithubCopilotPublisher publisher,
    IGithubCopilotConnectionRegistry connections,
    IGithubCopilotPrefixGate gate,
    IOptions<GithubCopilotOptions> options,
    TimeProvider timeProvider,
    ILogger<GithubCopilotLoginCoordinator> logger) : IGithubCopilotLoginCoordinator
{
    /// <summary>Bounded in-process publication attempts before returning 503 with the saved revision (D2).</summary>
    internal const int MaxPublicationAttempts = 3;

    /// <summary>How long a published flow stays answerable so a retried Complete returns the same identity.</summary>
    private static readonly TimeSpan PublishedFlowRetention = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan SlowDownPenalty = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, GithubCopilotLoginFlow> _flows = new(StringComparer.Ordinal);
    private readonly object _flowsLock = new();

    public async Task<GithubCopilotLoginStartResponse> StartAsync(
        string? prefix,
        string? clientId,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();

        if (!GithubCopilotProfileKey.TryNormalizePrefix(prefix, out string normalizedPrefix))
        {
            throw FieldError("prefix",
                $"prefix must be a lowercase slug of 1-{GithubCopilotProfileKey.MaxPrefixLength} characters (a-z, 0-9, single hyphens), or omitted for the unprefixed pair.");
        }

        GithubCopilotConnection? saved = store.Get(normalizedPrefix);
        string? effectiveClientId = GithubCopilotClientIdResolver.Resolve(
            clientId,
            saved?.ClientId,
            options.Value.ClientId,
            out bool invalidExplicit);

        if (invalidExplicit)
        {
            throw FieldError("clientId",
                $"clientId must be a non-blank, whitespace-free OAuth application client ID of at most {GithubCopilotClientIdResolver.MaxClientIdLength} characters.");
        }

        if (effectiveClientId is null)
        {
            throw FieldError("clientId",
                "clientId is required: supply your own GitHub OAuth application's client ID (device flow needs a client ID, not a client secret).");
        }

        PurgeExpiredFlows();

        using (await gate.AcquireAsync(normalizedPrefix, cancellationToken))
        {
            lock (_flowsLock)
            {
                if (FindActiveFlowForPrefix(normalizedPrefix) is not null)
                {
                    throw new GithubCopilotProviderConflictException(
                        $"A login for prefix '{DescribePrefix(normalizedPrefix)}' is already pending. Complete or let it expire before starting another.");
                }
            }

            GithubDeviceCode deviceCode;
            try
            {
                deviceCode = await oauthClient.StartDeviceFlowAsync(effectiveClientId, cancellationToken);
            }
            catch (GithubCopilotClientRejectedException ex)
            {
                throw FieldError("clientId", ex.Message);
            }
            DateTimeOffset now = timeProvider.GetUtcNow();

            var flow = new GithubCopilotLoginFlow
            {
                LoginId = Guid.NewGuid().ToString("N"),
                Prefix = normalizedPrefix,
                ClientId = effectiveClientId,
                DeviceCode = deviceCode.DeviceCode,
                UserCode = deviceCode.UserCode,
                VerificationUri = deviceCode.VerificationUri,
                ExpiresAtUtc = deviceCode.ExpiresAtUtc,
                IntervalSeconds = Math.Max(1, deviceCode.IntervalSeconds),
                NextPollAtUtc = now + TimeSpan.FromSeconds(Math.Max(1, deviceCode.IntervalSeconds))
            };

            lock (_flowsLock)
            {
                _flows[flow.LoginId] = flow;
            }

            var response = new GithubCopilotLoginStartResponse(
                flow.LoginId,
                flow.Prefix,
                flow.ClientId,
                ProviderKeys(flow.Prefix),
                flow.VerificationUri,
                flow.UserCode,
                flow.ExpiresAtUtc,
                flow.IntervalSeconds,
                CompleteUrl(flow.LoginId),
                Message: string.Empty);

            return response with { Message = GithubCopilotMessages.Start(response) };
        }
    }

    public async Task<GithubCopilotLoginCompleteResponse> CompleteAsync(string loginId, CancellationToken cancellationToken)
    {
        EnsureEnabled();

        GithubCopilotLoginFlow? flow;
        lock (_flowsLock)
        {
            flow = _flows.GetValueOrDefault(loginId);
        }

        if (flow is null)
        {
            return Failed(loginId, prefix: string.Empty, "login_not_found");
        }

        using (await gate.AcquireAsync(flow.Prefix, cancellationToken))
        {
            DateTimeOffset now = timeProvider.GetUtcNow();

            if (flow.State == GithubCopilotLoginState.Published)
            {
                return Connected(flow);
            }

            if (flow.State == GithubCopilotLoginState.Failed)
            {
                return Failed(flow.LoginId, flow.Prefix, flow.FailureCode ?? "login_failed");
            }

            if (flow.State == GithubCopilotLoginState.Pending && now >= flow.ExpiresAtUtc)
            {
                return Fail(flow, "expired_token");
            }

            if (flow.State == GithubCopilotLoginState.Pending)
            {
                int wait = flow.RetryAfterSeconds(now);
                if (wait > 0)
                {
                    return Pending(flow, wait);
                }

                GithubTokenPollResult poll;
                try
                {
                    poll = await oauthClient.PollTokenAsync(flow.ClientId, flow.DeviceCode, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "GitHub token poll failed for login {LoginId}", flow.LoginId);
                    return Retryable(flow, "github_unavailable");
                }

                switch (poll.Kind)
                {
                    case GithubTokenPollKind.Pending:
                        flow.NextPollAtUtc = now + TimeSpan.FromSeconds(flow.IntervalSeconds);
                        return Pending(flow, flow.IntervalSeconds);

                    case GithubTokenPollKind.SlowDown:
                        flow.IntervalSeconds = Math.Max(1, poll.IntervalSeconds ?? flow.IntervalSeconds) +
                                                (int)SlowDownPenalty.TotalSeconds;
                        flow.NextPollAtUtc = now + TimeSpan.FromSeconds(flow.IntervalSeconds);
                        logger.LogDebug("GitHub asked login {LoginId} to slow down; interval now {Interval}s", flow.LoginId, flow.IntervalSeconds);
                        return Pending(flow, flow.IntervalSeconds);

                    case GithubTokenPollKind.Denied:
                        return Fail(flow, "access_denied");

                    case GithubTokenPollKind.Expired:
                        return Fail(flow, "expired_token");

                    case GithubTokenPollKind.Rejected:
                        return Fail(flow, "invalid_grant");

                    case GithubTokenPollKind.Success:
                        flow.AccessToken = poll.AccessToken;
                        flow.RefreshToken = poll.RefreshToken;
                        flow.AccessTokenExpiresAtUtc = poll.AccessTokenExpiresAtUtc ?? flow.AccessTokenExpiresAtUtc;
                        if (poll.IntervalSeconds is int interval)
                        {
                            flow.IntervalSeconds = Math.Max(1, interval);
                        }

                        flow.State = GithubCopilotLoginState.TokenReceived;
                        break;
                }
            }

            // The per-prefix gate serializes this prefix, so no other login/refresh/delete can interleave with
            // the commit below. Flow state is only mutated under that gate.
            if (flow.State == GithubCopilotLoginState.Saved)
            {
                return Publish(flow);
            }

            if (flow.AccessToken is null)
            {
                return Failed(flow.LoginId, flow.Prefix, "invalid_grant");
            }

            if (flow.GitHubUser is null)
            {
                GithubUser? user;
                try
                {
                    user = await oauthClient.GetUserAsync(flow.AccessToken, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "GitHub user lookup failed for login {LoginId}", flow.LoginId);
                    return Retryable(flow, "github_unavailable");
                }

                if (user is null || string.IsNullOrWhiteSpace(user.Login))
                {
                    logger.LogWarning("GitHub user lookup for login {LoginId} returned no identity", flow.LoginId);
                    return Retryable(flow, "github_unavailable");
                }

                flow.GitHubUser = user.Login;
            }

            if (flow.ApiOrigin is null)
            {
                GithubCopilotEntitlement? entitlement;
                try
                {
                    entitlement = await oauthClient.GetCopilotEntitlementAsync(flow.AccessToken, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Copilot entitlement lookup failed for login {LoginId}", flow.LoginId);
                    return Retryable(flow, "github_unavailable");
                }

                if (entitlement is null || !entitlement.ChatEnabled)
                {
                    return Fail(flow, "copilot_not_enabled");
                }

                if (!GithubCopilotTransportPolicy.TryParseAllowedOrigin(entitlement.ApiOrigin, out Uri origin))
                {
                    logger.LogWarning("Copilot entitlement for login {LoginId} returned an unsupported API origin", flow.LoginId);
                    return Fail(flow, "invalid_api_origin");
                }

                flow.ApiOrigin = origin.GetLeftPart(UriPartial.Authority);
            }

            GithubCopilotConnection? existing = store.Get(flow.Prefix);
            var connection = new GithubCopilotConnection(
                Prefix: flow.Prefix,
                ConnectionId: flow.ConnectionId ?? Guid.NewGuid().ToString("N"),
                GitHubUser: flow.GitHubUser!,
                ClientId: flow.ClientId,
                ApiOrigin: flow.ApiOrigin!,
                AccessTokenCiphertext: protector.Protect(flow.AccessToken),
                RefreshTokenCiphertext: string.IsNullOrEmpty(flow.RefreshToken)
                    ? null
                    : protector.Protect(flow.RefreshToken),
                ExpiresAtUtc: flow.AccessTokenExpiresAtUtc?.ToString("O"),
                Revision: (existing?.Revision ?? 0) + 1);

            try
            {
                store.Upsert(connection);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not save GitHub Copilot connection for login {LoginId}", flow.LoginId);
                return Retryable(flow, "save_failed");
            }

            flow.ConnectionId = connection.ConnectionId;
            flow.Revision = connection.Revision;
            flow.State = GithubCopilotLoginState.Saved;

            return Publish(flow);
        }
    }

    private GithubCopilotLoginCompleteResponse Publish(GithubCopilotLoginFlow flow)
    {
        GithubCopilotConnection? saved = store.Get(flow.Prefix);
        if (saved is null || flow.Revision is null)
        {
            return Fail(flow, "save_failed");
        }

        string? failure = null;
        for (int attempt = 1; attempt <= MaxPublicationAttempts; attempt++)
        {
            GithubCopilotPublication publication;
            try
            {
                publication = publisher.Publish(saved);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Publication attempt {Attempt} failed for login {LoginId}", attempt, flow.LoginId);
                failure = ex.Message;
                continue;
            }

            if (publication.Published)
            {
                flow.State = GithubCopilotLoginState.Published;
                return Connected(flow);
            }

            failure = publication.FailureMessage;
            logger.LogWarning(
                "Publication attempt {Attempt} for login {LoginId} did not succeed: {Failure}",
                attempt,
                flow.LoginId,
                failure);
        }

        return new GithubCopilotLoginCompleteResponse(
            flow.LoginId,
            flow.Prefix,
            "saved_not_published",
            ProviderKeys(flow.Prefix),
            flow.ConnectionId,
            flow.Revision,
            PublishedRevision: null,
            PublicationReady: false,
            RetryAfterSeconds: null,
            GithubCopilotMessages.SavedNotPublished(flow.ConnectionId ?? string.Empty, flow.Revision ?? 0),
            ErrorCode: "publication_failed");
    }

    private GithubCopilotLoginCompleteResponse Connected(GithubCopilotLoginFlow flow)
    {
        GithubCopilotPublishedConnection? published = connections.GetByPrefix(flow.Prefix);
        return new GithubCopilotLoginCompleteResponse(
            flow.LoginId,
            flow.Prefix,
            "connected",
            ProviderKeys(flow.Prefix),
            flow.ConnectionId,
            flow.Revision,
            published?.Revision,
            PublicationReady: published is not null && published.Revision == flow.Revision,
            RetryAfterSeconds: null,
            GithubCopilotMessages.Connected(flow.ConnectionId ?? string.Empty, flow.Prefix));
    }

    private GithubCopilotLoginCompleteResponse Pending(GithubCopilotLoginFlow flow, int retryAfterSeconds) =>
        new(
            flow.LoginId,
            flow.Prefix,
            "pending",
            ProviderKeys(flow.Prefix),
            ConnectionId: null,
            SavedRevision: null,
            PublishedRevision: null,
            PublicationReady: false,
            retryAfterSeconds,
            GithubCopilotMessages.Pending(flow.VerificationUri, flow.UserCode, CompleteUrl(flow.LoginId), retryAfterSeconds));

    private GithubCopilotLoginCompleteResponse Retryable(GithubCopilotLoginFlow flow, string code) =>
        new(
            flow.LoginId,
            flow.Prefix,
            "retryable",
            ProviderKeys(flow.Prefix),
            flow.ConnectionId,
            flow.Revision,
            PublishedRevision: null,
            PublicationReady: false,
            RetryAfterSeconds: flow.IntervalSeconds,
            GithubCopilotMessages.Failed(code),
            ErrorCode: code);

    // A terminal failure is retained long enough for the operator to read the reason; the flow can no longer
    // be completed and no longer blocks a new start.
    private GithubCopilotLoginCompleteResponse Fail(GithubCopilotLoginFlow flow, string code)
    {
        flow.State = GithubCopilotLoginState.Failed;
        flow.FailureCode = code;
        flow.FailureMessage = GithubCopilotMessages.Failed(code);
        flow.AccessToken = null;
        flow.RefreshToken = null;
        flow.DeviceCode = string.Empty;
        logger.LogInformation("GitHub Copilot login {LoginId} failed with {Code}", flow.LoginId, code);
        return new GithubCopilotLoginCompleteResponse(
            flow.LoginId,
            flow.Prefix,
            "failed",
            ProviderKeys(flow.Prefix),
            ConnectionId: null,
            SavedRevision: null,
            PublishedRevision: null,
            PublicationReady: false,
            RetryAfterSeconds: null,
            flow.FailureMessage,
            ErrorCode: code);
    }

    private static GithubCopilotLoginCompleteResponse Failed(string loginId, string prefix, string code) =>
        new(
            loginId,
            prefix,
            "failed",
            prefix.Length > 0 ? ProviderKeys(prefix) : [],
            ConnectionId: null,
            SavedRevision: null,
            PublishedRevision: null,
            PublicationReady: false,
            RetryAfterSeconds: null,
            GithubCopilotMessages.Failed(code),
            ErrorCode: code);

    private GithubCopilotLoginFlow? FindActiveFlowForPrefix(string prefix)
    {
        foreach (GithubCopilotLoginFlow flow in _flows.Values)
        {
            if (flow.Prefix == prefix &&
                flow.State is GithubCopilotLoginState.Pending or GithubCopilotLoginState.TokenReceived or GithubCopilotLoginState.Saved)
            {
                return flow;
            }
        }

        return null;
    }

    private void PurgeExpiredFlows()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        lock (_flowsLock)
        {
            foreach (string loginId in _flows.Keys.ToArray())
            {
                GithubCopilotLoginFlow flow = _flows[loginId];
                DateTimeOffset purgeAt = flow.State switch
                {
                    GithubCopilotLoginState.Pending or GithubCopilotLoginState.TokenReceived or GithubCopilotLoginState.Saved
                        => flow.ExpiresAtUtc,
                    _ => flow.ExpiresAtUtc + PublishedFlowRetention
                };

                if (now > purgeAt)
                {
                    _flows.Remove(loginId);
                }
            }
        }
    }

    private void EnsureEnabled()
    {
        if (!options.Value.Enabled)
        {
            throw new GithubCopilotFeatureDisabledException(
                "GitHub Copilot is disabled (ENABLE_GITHUB_COPILOT is not true), so login endpoints are unavailable.");
        }
    }

    private static string[] ProviderKeys(string prefix) =>
    [
        GithubCopilotProfileKey.Key(prefix, ApiDialect.Anthropic),
        GithubCopilotProfileKey.Key(prefix, ApiDialect.OpenAi)
    ];

    private static string CompleteUrl(string loginId) => $"/admin/github-copilot/logins/{loginId}/complete";

    private static string DescribePrefix(string prefix) => string.IsNullOrEmpty(prefix) ? "(unprefixed)" : prefix;

    private static ValidationException FieldError(string property, string message) =>
        new([new ValidationFailure(property, message)]);
}
