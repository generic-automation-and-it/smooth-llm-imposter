using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.GithubCopilot;

namespace SmoothLlmImposter.Infrastructure.Persistence.GithubCopilot;

/// <summary>
/// Feature-gated Copilot store bootstrap. With the feature disabled it opens no Copilot database (R6/AC01).
/// When enabled it ensures the schema and performs a single probe round-trip at startup, exercising the
/// Microsoft.Data.Sqlite store path before real connections exist. The probe writes and deletes its own
/// uniquely-prefixed row, so it never deletes an existing database and leaves no residue. Real connection
/// hydration replaces the probe once the feature store hosts credentials.
/// </summary>
internal sealed class GithubCopilotBootstrap(
    ILogger<GithubCopilotBootstrap> logger,
    IOptions<GithubCopilotOptions> options,
    IGithubCopilotConnectionStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogDebug("GitHub Copilot feature disabled; no Copilot store opened.");
            return Task.CompletedTask;
        }

        store.EnsureCreated();

        var probe = new GithubCopilotConnection(
            Prefix: $"probe-{Guid.NewGuid():N}",
            ConnectionId: Guid.NewGuid().ToString("N"),
            GitHubUser: "native-aot",
            ClientId: "probe",
            ApiOrigin: "https://api.enterprise.githubcopilot.com",
            AccessTokenCiphertext: "cipher:access",
            RefreshTokenCiphertext: null,
            ExpiresAtUtc: null,
            Revision: 1);

        store.Upsert(probe);
        GithubCopilotConnection? read = store.Get(probe.Prefix);
        if (read is null || read.ConnectionId != probe.ConnectionId)
        {
            throw new InvalidOperationException("GitHub Copilot SQLite store probe failed.");
        }
        store.Delete(probe.Prefix);

        logger.LogInformation("GitHub Copilot store ready (probe ok)");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
