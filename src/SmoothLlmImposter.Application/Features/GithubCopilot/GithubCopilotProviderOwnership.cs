using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Domain.GithubCopilot;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Ownership rules for reserved provider keys, consulted by every generic provider mutation (D1): the
/// feature owns identity and creation, while <c>Models</c> and <c>Enabled</c> stay operator-editable.
/// </summary>
public sealed class GithubCopilotProviderOwnership(
    IOptions<GithubCopilotOptions> options,
    IGithubCopilotConnectionStore store,
    IGithubCopilotPrefixGate gate)
{
    public bool Enabled => options.Value.Enabled;

    public static bool TryGetProfile(string key, out GithubCopilotProfile profile)
    {
        if (GithubCopilotProfileKey.TryParse(key, out GithubCopilotProfile? parsed))
        {
            profile = parsed!;
            return true;
        }

        profile = null!;
        return false;
    }

    /// <summary>
    /// True when a durable connection row exists for the prefix — including an expired or
    /// reauthentication-required row. The row, not a cache of usable tokens, is the ownership index.
    /// </summary>
    public bool HasDurableConnection(string prefix) => store.Exists(prefix);

    public Task<IDisposable> AcquireAsync(string prefix, CancellationToken cancellationToken) =>
        gate.AcquireAsync(prefix, cancellationToken);

    /// <summary>Fails closed before any store access when the feature is disabled.</summary>
    public void EnsureFeatureEnabled()
    {
        if (!Enabled)
        {
            throw new GithubCopilotFeatureDisabledException(
                "GitHub Copilot is disabled (ENABLE_GITHUB_COPILOT is not true), so reserved provider keys cannot be mutated. " +
                "Enable the feature before changing them.");
        }
    }
}
