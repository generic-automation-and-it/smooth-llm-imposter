using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.GithubCopilot;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Builds the safe admin status view of a saved connection. Read-only: it never writes, reconciles or performs
/// a GitHub call. It separates <c>publicationReady</c> (saved revision and provider pair published — a restart
/// recovers it) from per-provider <c>routingReady</c> (Enabled, usable credentials, an applicable mapping), so a
/// missing mapping is never misreported as a publication failure (D2/AC12).
/// </summary>
public sealed class GithubCopilotConnectionStatusReader(
    IProviderRegistry registry,
    IGithubCopilotConnectionRegistry connections,
    IGithubCopilotConnectionStore store,
    IOptions<GithubCopilotOptions> options,
    TimeProvider timeProvider)
{
    public GithubCopilotConnectionStatus[] List()
    {
        if (!options.Value.Enabled)
        {
            return [];
        }

        return store.List().Select(Build).ToArray();
    }

    public GithubCopilotConnectionStatus? Get(string connectionId)
    {
        if (!options.Value.Enabled)
        {
            return null;
        }

        GithubCopilotConnection? saved = store.List().FirstOrDefault(connection => connection.ConnectionId == connectionId);
        return saved is null ? null : Build(saved);
    }

    public GithubCopilotConnectionStatus Build(GithubCopilotConnection saved)
    {
        GithubCopilotPublishedConnection? published = connections.GetByPrefix(saved.Prefix);
        bool publishedRevisionMatches = published is not null && published.Revision == saved.Revision;

        string[] keys =
        [
            GithubCopilotProfileKey.Key(saved.Prefix, ApiDialect.Anthropic),
            GithubCopilotProfileKey.Key(saved.Prefix, ApiDialect.OpenAi)
        ];

        bool pairPublished = publishedRevisionMatches && keys.All(key =>
            GithubCopilotProfileKey.TryParse(key, out GithubCopilotProfile? profile) &&
            registry.TryGet(key, out ProviderOptions? provider) &&
            ApiDialectParser.TryParse(provider!.Dialect, out ApiDialect dialect) &&
            dialect == profile!.Dialect);

        bool usable = publishedRevisionMatches && published!.HasUsableCredentialAt(timeProvider.GetUtcNow());

        GithubCopilotProviderStatus[] providers = keys.Select(key =>
        {
            bool exists = registry.TryGet(key, out ProviderOptions? provider);
            bool enabled = exists && provider!.Enabled;
            bool hasMapping = exists && provider!.Models.Count > 0;
            return new GithubCopilotProviderStatus(key, enabled, enabled && usable && hasMapping);
        }).ToArray();

        bool reauthenticationRequired = published?.State == GithubCopilotConnectionState.ReauthenticationRequired;

        DateTimeOffset? expiresAt = null;
        if (!string.IsNullOrEmpty(saved.ExpiresAtUtc) && DateTimeOffset.TryParse(saved.ExpiresAtUtc, out DateTimeOffset parsed))
        {
            expiresAt = parsed;
        }

        return new GithubCopilotConnectionStatus(
            saved.ConnectionId,
            saved.Prefix,
            saved.GitHubUser,
            saved.ClientId,
            saved.ApiOrigin,
            saved.Revision,
            publishedRevisionMatches ? published!.Revision : null,
            pairPublished,
            reauthenticationRequired,
            expiresAt,
            providers,
            GithubCopilotMessages.ConnectionStatus(saved.Prefix, pairPublished, providers, reauthenticationRequired));
    }
}
