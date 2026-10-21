namespace SmoothLlmImposter.Application.Common.Clients.GithubCopilot;

/// <summary>
/// One model advertised by the account's Copilot endpoint. <see cref="Endpoints"/> are the wire surfaces the
/// account policy advertises (e.g. <c>messages</c>, <c>responses</c>); Chat Completions is never a setup target
/// in this version. Discovery never creates a mapping or enables a provider.
/// </summary>
public sealed record GithubCopilotModel(string Id, string[] Endpoints, string? Policy);

/// <summary>
/// Uncached plural model-discovery client for the account-discovered Copilot API origin. The managed Bearer
/// token is the only credential; the origin must already be validated by the transport policy.
/// </summary>
public interface IGithubCopilotModelsClient
{
    Task<GithubCopilotModel[]> ListModelsAsync(string accessToken, string apiOrigin, CancellationToken cancellationToken);
}
