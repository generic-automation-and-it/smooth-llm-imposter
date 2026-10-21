using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Persistence;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Startup recovery path for D2: after configured providers are seeded, hydrate every durable connection into
/// the published registry and re-materialize missing provider pairs before the host serves requests. A row
/// committed before a publication interruption becomes routable without another token exchange; an
/// undecryptable or unpublishable row fails startup with an actionable message rather than being dropped.
/// </summary>
public interface IGithubCopilotStartupCoordinator
{
    void Start();
}

internal sealed class GithubCopilotStartupCoordinator(
    IGithubCopilotConnectionStore store,
    IGithubCopilotPublisher publisher,
    IOptions<GithubCopilotOptions> options,
    ILogger<GithubCopilotStartupCoordinator> logger) : IGithubCopilotStartupCoordinator
{
    public void Start()
    {
        if (!options.Value.Enabled)
        {
            logger.LogDebug("GitHub Copilot feature disabled; no connection hydration performed.");
            return;
        }

        store.EnsureCreated();

        GithubCopilotConnection[] saved = store.List();
        foreach (GithubCopilotConnection connection in saved)
        {
            GithubCopilotPublication publication = publisher.Publish(connection);
            if (!publication.Published)
            {
                throw new InvalidOperationException(
                    $"GitHub Copilot connection for prefix '{Describe(connection.Prefix)}' (revision {connection.Revision}) " +
                    $"could not be published at startup: {publication.FailureMessage}. Restore the Copilot data volume " +
                    "or start a new login; the saved row is retained.");
            }
        }

        logger.LogInformation("GitHub Copilot startup hydration complete: {ConnectionCount} connection(s) published.", saved.Length);
    }

    private static string Describe(string prefix) => string.IsNullOrEmpty(prefix) ? "(unprefixed)" : prefix;
}
