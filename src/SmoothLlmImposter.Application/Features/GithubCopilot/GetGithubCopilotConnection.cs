using FluentValidation;
using Mediator;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Step 05 — read one connection's safe status. <see cref="Handler"/> returns null for an unknown connection,
/// which the endpoint maps to 404.
/// </summary>
public static class GetGithubCopilotConnection
{
    public sealed record Request(string ConnectionId) : IRequest<GithubCopilotConnectionStatus?>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.ConnectionId).NotEmpty().MaximumLength(64);
        }
    }

    public sealed class Handler(GithubCopilotConnectionStatusReader statusReader)
        : IRequestHandler<Request, GithubCopilotConnectionStatus?>
    {
        public ValueTask<GithubCopilotConnectionStatus?> Handle(Request request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(statusReader.Get(request.ConnectionId));
    }
}
