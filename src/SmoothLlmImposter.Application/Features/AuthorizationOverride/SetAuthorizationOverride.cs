using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.AuthorizationOverride;

/// <summary>
/// Arms (enables) the passthrough authorization override for a dialect. Refuses with
/// <see cref="SetAuthorizationOverrideResult.NoActiveCredential"/> when the dialect has no active stored
/// credential to present (LADR-005); the switch stays OFF in that case.
/// </summary>
public static class SetAuthorizationOverride
{
    public sealed record Request(string Dialect, string? Actor) : IRequest<SetAuthorizationOverrideResult>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Dialect).Must(x => ApiDialectParser.TryParse(x, out _)).WithMessage("Dialect must be 'openai' or 'anthropic'.");
        }
    }

    public sealed class Handler(
        IAuthorizationOverrideSwitch overrideSwitch,
        ICredentialStore store,
        ILogger<Handler> logger) : IRequestHandler<Request, SetAuthorizationOverrideResult>
    {
        public async ValueTask<SetAuthorizationOverrideResult> Handle(Request request, CancellationToken cancellationToken)
        {
            ApiDialect dialect = ApiDialectParser.Parse(request.Dialect);
            string token = dialect.ToToken();

            ProviderCredential? active = await store.GetActiveAsync(dialect, cancellationToken);
            if (active is null)
            {
                return new SetAuthorizationOverrideResult(Armed: false, new AuthorizationOverrideState(token, Enabled: false));
            }

            overrideSwitch.Enable(dialect);

            // NFR-003 mandates exactly one secret-free Information audit line per toggle (actor + dialect + action).
            // This is an intentional exception to the "Information = start/end only" logging default.
            logger.LogInformation(
                "Passthrough authorization override enabled by {Actor} for dialect {Dialect}",
                request.Actor ?? "unknown",
                token);

            return new SetAuthorizationOverrideResult(Armed: true, new AuthorizationOverrideState(token, Enabled: true));
        }
    }
}
