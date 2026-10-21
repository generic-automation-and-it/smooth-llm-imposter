using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.GithubCopilot;
using SmoothLlmImposter.Application.Common.Persistence;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Refreshes expiring connections outside the inference path (R8). It refreshes ahead of expiry, rotates the
/// access/refresh pair in one transaction, then publishes the new revision. A terminal refresh failure or an
/// expiring token with no refresh token marks the connection reauthentication-required (fail closed); a
/// replacement pair that cannot be saved is never silently dropped in favour of a possibly revoked old pair.
/// </summary>
public interface IGithubCopilotRefreshCoordinator
{
    Task<int> RefreshDueAsync(CancellationToken cancellationToken);
}

internal sealed class GithubCopilotRefreshCoordinator(
    IGithubOAuthClient oauthClient,
    IGithubCopilotConnectionStore store,
    IGithubCopilotSecretProtector protector,
    IGithubCopilotPublisher publisher,
    IGithubCopilotConnectionRegistry connections,
    IGithubCopilotPrefixGate gate,
    IOptions<GithubCopilotOptions> options,
    TimeProvider timeProvider,
    ILogger<GithubCopilotRefreshCoordinator> logger) : IGithubCopilotRefreshCoordinator
{
    internal static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(10);

    public async Task<int> RefreshDueAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return 0;
        }

        int refreshed = 0;
        foreach (GithubCopilotPublishedConnection candidate in connections.Snapshot())
        {
            if (!IsDue(candidate, timeProvider.GetUtcNow()))
            {
                continue;
            }

            using (await gate.AcquireAsync(candidate.Prefix, cancellationToken))
            {
                GithubCopilotPublishedConnection? current = connections.GetByPrefix(candidate.Prefix);
                if (current is null ||
                    current.Revision != candidate.Revision ||
                    current.State != GithubCopilotConnectionState.Active ||
                    !IsDue(current, timeProvider.GetUtcNow()))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(current.RefreshToken))
                {
                    // Expiring token with no refresh token: malformed expiry semantics are never treated as
                    // immortality, and the connection must not keep presenting a soon-dead token.
                    connections.InvalidateIfCurrent(current.Prefix, current.Revision);
                    logger.LogWarning(
                        "GitHub Copilot connection {ConnectionId} expires without a refresh token; marking reauthentication required.",
                        current.ConnectionId);
                    continue;
                }

                GithubTokenPollResult refresh;
                try
                {
                    refresh = await oauthClient.RefreshAsync(current.ClientId, current.RefreshToken, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "GitHub Copilot refresh failed for connection {ConnectionId}", current.ConnectionId);
                    continue; // network failure: keep the current token usable while it remains valid
                }

                if (refresh.Kind != GithubTokenPollKind.Success || string.IsNullOrEmpty(refresh.AccessToken))
                {
                    connections.InvalidateIfCurrent(current.Prefix, current.Revision);
                    logger.LogWarning(
                        "GitHub Copilot refresh for connection {ConnectionId} ended with {Kind}; marking reauthentication required.",
                        current.ConnectionId,
                        refresh.Kind);
                    continue;
                }

                GithubCopilotConnection? saved = store.Get(current.Prefix);
                if (saved is null)
                {
                    connections.InvalidateIfCurrent(current.Prefix, current.Revision);
                    logger.LogWarning(
                        "GitHub Copilot refresh for connection {ConnectionId} found no durable row; marking reauthentication required.",
                        current.ConnectionId);
                    continue;
                }

                var replacement = saved with
                {
                    AccessTokenCiphertext = protector.Protect(refresh.AccessToken),
                    RefreshTokenCiphertext = string.IsNullOrEmpty(refresh.RefreshToken)
                        ? saved.RefreshTokenCiphertext
                        : protector.Protect(refresh.RefreshToken),
                    ExpiresAtUtc = refresh.AccessTokenExpiresAtUtc?.ToString("O"),
                    Revision = saved.Revision + 1
                };

                try
                {
                    store.Upsert(replacement);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Cancellation only comes from host shutdown: propagate it and leave the published pair as it
                    // was. If GitHub already rotated, the next boot's refresh fails and fails closed then.
                    // GitHub may already have revoked the old pair; fail closed rather than advertise it.
                    connections.InvalidateIfCurrent(current.Prefix, current.Revision);
                    logger.LogError(ex, "Could not save the refreshed GitHub Copilot pair for connection {ConnectionId}; marking reauthentication required.", current.ConnectionId);
                    continue;
                }

                if (!publisher.Publish(replacement).Published)
                {
                    connections.InvalidateIfCurrent(current.Prefix, current.Revision);
                    logger.LogError("Could not publish the refreshed GitHub Copilot pair for connection {ConnectionId}; marking reauthentication required.", current.ConnectionId);
                    continue;
                }

                refreshed++;
            }
        }

        return refreshed;
    }

    private static bool IsDue(GithubCopilotPublishedConnection connection, DateTimeOffset now) =>
        connection.State == GithubCopilotConnectionState.Active &&
        connection.ExpiresAtUtc is DateTimeOffset expiresAt &&
        expiresAt <= now + RefreshAhead;
}

/// <summary>
/// Periodic refresh driver. Runs one pass shortly after startup (a restarted host may already be due), then
/// on a fixed cadence; each pass is cheap because it only inspects the published snapshot.
/// </summary>
internal sealed class GithubCopilotRefreshService(
    IGithubCopilotRefreshCoordinator coordinator,
    IOptions<GithubCopilotOptions> options,
    ILogger<GithubCopilotRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogDebug("GitHub Copilot feature disabled; token refresher not started.");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int refreshed = await coordinator.RefreshDueAsync(stoppingToken);
                if (refreshed > 0)
                {
                    logger.LogInformation("GitHub Copilot token refresh complete: {RefreshedCount} connection(s) rotated.", refreshed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "GitHub Copilot token refresh pass failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
