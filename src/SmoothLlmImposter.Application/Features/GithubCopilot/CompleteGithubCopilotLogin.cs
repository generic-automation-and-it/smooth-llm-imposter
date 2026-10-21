using FluentValidation;
using Mediator;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Step 02 — poll the pending login once and, on success, validate identity/entitlement, save the credential
/// and publish its revision. The login ID is the only input: a client ID is frozen into the flow at start and
/// can never be replaced here. The response's <c>message</c> carries the next curl step for every outcome.
/// </summary>
public static class CompleteGithubCopilotLogin
{
    public sealed record Request(string LoginId) : IRequest<GithubCopilotLoginCompleteResponse>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.LoginId).NotEmpty().MaximumLength(64);
        }
    }

    public sealed class Handler(IGithubCopilotLoginCoordinator coordinator)
        : IRequestHandler<Request, GithubCopilotLoginCompleteResponse>
    {
        public ValueTask<GithubCopilotLoginCompleteResponse> Handle(Request request, CancellationToken cancellationToken) =>
            new(coordinator.CompleteAsync(request.LoginId, cancellationToken));
    }
}
