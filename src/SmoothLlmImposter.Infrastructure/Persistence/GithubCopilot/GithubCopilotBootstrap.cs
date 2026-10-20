using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SmoothLlmImposter.Infrastructure.Persistence.GithubCopilot;

/// <summary>
/// Feature-gated Copilot store bootstrap. Active only when <c>ENABLE_GITHUB_COPILOT</c> is truthy; with the
/// feature off it opens no Copilot database (R6/AC01). When on it ensures the schema and performs a single
/// probe round-trip at startup so a NativeAOT image proves the Microsoft.Data.Sqlite store path before it is
/// exercised by real connections. The probe writes and deletes its own row, leaving no residue. Real connection
/// hydration replaces this once the feature store hosts credentials.
/// </summary>
internal sealed class GithubCopilotBootstrap(
    ILogger<GithubCopilotBootstrap> logger,
    Microsoft.Extensions.Configuration.IConfiguration configuration)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!FeatureEnabled())
        {
            logger.LogDebug("GitHub Copilot feature disabled; no Copilot store opened.");
            return Task.CompletedTask;
        }

        string databasePath = configuration["GithubCopilot:DatabasePath"]
            ?? Path.Combine(Path.GetTempPath(), $"copilot-spike-{Environment.ProcessId}.db");
        var store = new SqliteGithubCopilotConnectionStore(
            SqliteGithubCopilotConnectionStore.BuildConnectionString(databasePath));

        // Never delete the database here: EnsureCreated is idempotent and persisted connections must
        // survive restart (R7/AC13). The probe below writes and deletes its own row, so nothing is left.
        store.EnsureCreated();

        var probe = new GithubCopilotConnectionRow(
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
        GithubCopilotConnectionRow? read = store.Get(probe.Prefix);
        if (read is null || read.ConnectionId != probe.ConnectionId)
        {
            throw new InvalidOperationException("GitHub Copilot SQLite store probe failed under NativeAOT.");
        }
        store.Delete(probe.Prefix);

        logger.LogInformation("GitHub Copilot store ready (AOT probe ok): {DatabasePath}", databasePath);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool FeatureEnabled()
    {
        string? value = Environment.GetEnvironmentVariable("ENABLE_GITHUB_COPILOT");
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
    }
}
