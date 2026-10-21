using FluentValidation;
using Mediator;
using SmoothLlmImposter.Domain.GithubCopilot;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Step 01 — start a GitHub device login for a provider prefix. The request supplies the operator's own
/// OAuth application client ID (device flow needs a client ID, not a client secret) and an optional prefix;
/// omitting the prefix selects the unprefixed pair and never marks a provider as the dialect default.
/// </summary>
public static class StartGithubCopilotLogin
{
    public sealed record Request(string? Prefix, string? ClientId) : IRequest<GithubCopilotLoginStartResponse>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Prefix)
                .Must(static prefix => GithubCopilotProfileKey.TryNormalizePrefix(prefix, out _))
                .WithMessage(
                    $"prefix must be a lowercase slug of 1-{GithubCopilotProfileKey.MaxPrefixLength} characters " +
                    "(a-z, 0-9, single hyphens), or omitted for the unprefixed pair.");

            RuleFor(x => x.ClientId)
                .Must(static clientId => clientId is null || GithubCopilotClientIdResolver.IsValid(clientId))
                .WithMessage(
                    $"clientId must be a non-blank, whitespace-free OAuth application client ID of at most " +
                    $"{GithubCopilotClientIdResolver.MaxClientIdLength} characters.");
        }
    }

    public sealed class Handler(IGithubCopilotLoginCoordinator coordinator)
        : IRequestHandler<Request, GithubCopilotLoginStartResponse>
    {
        public ValueTask<GithubCopilotLoginStartResponse> Handle(Request request, CancellationToken cancellationToken) =>
            new(coordinator.StartAsync(request.Prefix, request.ClientId, cancellationToken));
    }
}
