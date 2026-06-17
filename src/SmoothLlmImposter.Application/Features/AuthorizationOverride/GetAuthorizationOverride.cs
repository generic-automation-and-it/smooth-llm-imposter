using FluentValidation;
using Mediator;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.AuthorizationOverride;

/// <summary>Reports the current on/off state of a dialect's passthrough authorization override.</summary>
public static class GetAuthorizationOverride
{
    public sealed record Request(string Dialect) : IRequest<AuthorizationOverrideState>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Dialect).Must(x => ApiDialectParser.TryParse(x, out _)).WithMessage("Dialect must be 'openai' or 'anthropic'.");
        }
    }

    public sealed class Handler(IAuthorizationOverrideSwitch overrideSwitch) : IRequestHandler<Request, AuthorizationOverrideState>
    {
        public ValueTask<AuthorizationOverrideState> Handle(Request request, CancellationToken cancellationToken)
        {
            ApiDialect dialect = ApiDialectParser.Parse(request.Dialect);
            return ValueTask.FromResult(new AuthorizationOverrideState(dialect.ToToken(), overrideSwitch.IsEnabled(dialect)));
        }
    }
}
