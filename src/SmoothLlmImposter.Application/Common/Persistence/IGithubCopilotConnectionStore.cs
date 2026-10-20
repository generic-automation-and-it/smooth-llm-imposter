using SmoothLlmImposter.Application.Features.GithubCopilot;

namespace SmoothLlmImposter.Application.Common.Persistence;

/// <summary>
/// Durable store for GitHub Copilot account connections, one per normalized prefix. Implemented only in
/// Infrastructure; routing consumes hydrated snapshots and never touches this store on the inference path.
/// </summary>
public interface IGithubCopilotConnectionStore
{
    void EnsureCreated();
    void Upsert(GithubCopilotConnection connection);
    GithubCopilotConnection? Get(string prefix);
    bool Exists(string prefix);
    int Delete(string prefix);
}
