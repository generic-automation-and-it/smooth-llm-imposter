using FluentValidation;
using Mediator;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Connection list — safe durable metadata after restart, without another login. Read-only: it never
/// reconciles, publishes or contacts GitHub.
/// </summary>
public static class ListGithubCopilotConnections
{
    public sealed record Request : IRequest<GithubCopilotConnectionListResponse>;

    public sealed class Validator : AbstractValidator<Request>
    {
    }

    public sealed class Handler(GithubCopilotConnectionStatusReader statusReader)
        : IRequestHandler<Request, GithubCopilotConnectionListResponse>
    {
        public ValueTask<GithubCopilotConnectionListResponse> Handle(Request request, CancellationToken cancellationToken)
        {
            GithubCopilotConnectionStatus[] connections = statusReader.List();
            return ValueTask.FromResult(new GithubCopilotConnectionListResponse(
                connections,
                GithubCopilotMessages.List(connections)));
        }
    }
}
