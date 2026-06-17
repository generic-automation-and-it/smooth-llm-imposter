using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.AuthorizationOverride;

/// <summary>Disables the passthrough authorization override for a dialect (idempotent set-off).</summary>
public static class ClearAuthorizationOverride
{
    public sealed record Request(string Dialect, string? Actor) : IRequest<AuthorizationOverrideState>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Dialect).Must(x => ApiDialectParser.TryParse(x, out _)).WithMessage("Dialect must be 'openai' or 'anthropic'.");
        }
    }

    public sealed class Handler(
        IAuthorizationOverrideSwitch overrideSwitch,
        ILogger<Handler> logger) : IRequestHandler<Request, AuthorizationOverrideState>
    {
        public ValueTask<AuthorizationOverrideState> Handle(Request request, CancellationToken cancellationToken)
        {
            ApiDialect dialect = ApiDialectParser.Parse(request.Dialect);
            string token = dialect.ToToken();

            overrideSwitch.Disable(dialect);

            // NFR-003: one secret-free Information audit line per toggle (actor + dialect + action).
            logger.LogInformation(
                "Passthrough authorization override disabled by {Actor} for dialect {Dialect}",
                request.Actor ?? "unknown",
                token);

            return ValueTask.FromResult(new AuthorizationOverrideState(token, Enabled: false));
        }
    }
}
