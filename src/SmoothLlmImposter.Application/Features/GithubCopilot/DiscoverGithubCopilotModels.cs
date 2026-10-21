using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Common.Clients.GithubCopilot;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Step 03 — discover the models this account advertises, separated by the Messages/Responses surfaces it
/// supports. Copilot Chat is not a setup target, so chat-only endpoints are filtered out. Discovery never
/// creates a mapping, enables a target or reconciles state; the response's <c>message</c> points at the
/// existing secret-free <c>PUT /admin/providers/&lt;key&gt;</c> step.
/// </summary>
public static class DiscoverGithubCopilotModels
{
    public sealed record Request(string ConnectionId) : IRequest<GithubCopilotModelsDiscoveryResponse?>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.ConnectionId).NotEmpty().MaximumLength(64);
        }
    }

    public sealed class Handler(
        GithubCopilotConnectionStatusReader statusReader,
        IGithubCopilotConnectionRegistry connections,
        IGithubCopilotModelsClient modelsClient,
        TimeProvider timeProvider,
        ILogger<Handler> logger) : IRequestHandler<Request, GithubCopilotModelsDiscoveryResponse?>
    {
        public async ValueTask<GithubCopilotModelsDiscoveryResponse?> Handle(Request request, CancellationToken cancellationToken)
        {
            GithubCopilotConnectionStatus? status = statusReader.Get(request.ConnectionId);
            if (status is null)
            {
                return null;
            }

            GithubCopilotPublishedConnection? published = connections.GetByPrefix(status.Prefix);
            if (published is null ||
                !published.HasUsableCredentialAt(timeProvider.GetUtcNow()) ||
                published.State != GithubCopilotConnectionState.Active)
            {
                throw new GithubCopilotProviderConflictException(
                    $"Connection {request.ConnectionId} has no usable credential (saved but unpublished, expired, revoked or requiring reauthorization). " +
                    "Start a new login before discovering models.");
            }

            GithubCopilotModel[] models;
            try
            {
                models = await modelsClient.ListModelsAsync(published.AccessToken, published.ApiOrigin, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "GitHub Copilot model discovery failed for connection {ConnectionId}", request.ConnectionId);
                throw new GithubCopilotUpstreamException(
                    $"GitHub Copilot model discovery for connection {request.ConnectionId} failed: {ex.Message}. Retry the request; no login is needed.");
            }

            GithubCopilotModelResponse[] candidates = models
                .Select(static model => new GithubCopilotModelResponse(
                    model.Id,
                    model.Endpoints.Where(static endpoint => !IsChatEndpoint(endpoint)).ToArray(),
                    model.Policy))
                .Where(static model => model.Endpoints.Length > 0)
                .ToArray();

            return new GithubCopilotModelsDiscoveryResponse(
                request.ConnectionId,
                status.Providers.Select(static provider => provider.Key).ToArray(),
                candidates,
                GithubCopilotMessages.Models(request.ConnectionId, status.Providers));
        }

        private static bool IsChatEndpoint(string endpoint) =>
            endpoint.Equals("chat", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("chat_", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("chat-", StringComparison.OrdinalIgnoreCase);
    }
}
