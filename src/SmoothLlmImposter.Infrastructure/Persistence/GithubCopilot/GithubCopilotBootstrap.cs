using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.GithubCopilot;

namespace SmoothLlmImposter.Infrastructure.Persistence.GithubCopilot;

/// <summary>
/// Feature-gated Copilot store bootstrap. With the feature disabled it opens no Copilot database (R6/AC01).
/// When enabled it hydrates durable connections and re-materializes missing provider pairs before the host
/// serves requests (via <see cref="IGithubCopilotStartupCoordinator"/>), then verifies the store is writable with a
/// transaction that is always rolled back. The probe leaves no residue — even on a crash — and never removes
/// existing data.
/// </summary>
internal sealed class GithubCopilotBootstrap(
    ILogger<GithubCopilotBootstrap> logger,
    IOptions<GithubCopilotOptions> options,
    IGithubCopilotConnectionStore store,
    IGithubCopilotStartupCoordinator startupCoordinator) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogDebug("GitHub Copilot feature disabled; no Copilot store opened.");
            return Task.CompletedTask;
        }

        startupCoordinator.Start();
        store.VerifyWritable();

        logger.LogInformation("GitHub Copilot store ready (writable probe ok)");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
