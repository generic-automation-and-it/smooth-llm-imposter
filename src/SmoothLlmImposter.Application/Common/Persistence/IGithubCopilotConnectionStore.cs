using SmoothLlmImposter.Application.Features.GithubCopilot;

namespace SmoothLlmImposter.Application.Common.Persistence;

/// <summary>
/// Durable store for GitHub Copilot account connections, one per normalized prefix. Implemented only in
/// Infrastructure; routing consumes hydrated snapshots and never touches this store on the inference path.
/// </summary>
public interface IGithubCopilotConnectionStore
{
    void EnsureCreated();

    /// <summary>
    /// Exercises one write round-trip inside a transaction that is always rolled back, leaving no residue even
    /// if the process dies mid-probe. Used by the startup bootstrap to prove the store is writable before
    /// serving; it must never delete or mutate existing data.
    /// </summary>
    void VerifyWritable();

    void Upsert(GithubCopilotConnection connection);

    GithubCopilotConnection? Get(string prefix);

    /// <summary>Every saved connection, ordered by prefix. Used for startup hydration and the admin list.</summary>
    GithubCopilotConnection[] List();

    bool Exists(string prefix);
    int Delete(string prefix);
}
