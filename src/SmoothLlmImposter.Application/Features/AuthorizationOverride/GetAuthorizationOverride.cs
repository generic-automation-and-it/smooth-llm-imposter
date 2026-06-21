using FluentValidation;
using Mediator;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.AuthorizationOverride;

/// <summary>Reports the current on/off state of a dialect's passthrough authorization override.</summary>
public static class GetAuthorizationOverride
{
    public sealed record Request(string Dialect, string? ProviderName) : IRequest<AuthorizationOverrideState>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Dialect).Must(x => ApiDialectParser.TryParse(x, out _)).WithMessage("Dialect must be 'openai' or 'anthropic'.");
            RuleFor(x => x.ProviderName).MaximumLength(128);
        }
    }

    public sealed class Handler(
        IAuthorizationOverrideSwitch overrideSwitch,
        IProviderCatalog catalog) : IRequestHandler<Request, AuthorizationOverrideState>
    {
        public ValueTask<AuthorizationOverrideState> Handle(Request request, CancellationToken cancellationToken)
        {
            ApiDialect dialect = ApiDialectParser.Parse(request.Dialect);
            ProviderRoute provider = ProviderAddressResolver.Resolve(catalog, dialect, request.ProviderName, nameof(request.ProviderName));
            string providerName = provider.CredentialProviderName;
            return ValueTask.FromResult(new AuthorizationOverrideState(dialect.ToToken(), providerName, overrideSwitch.IsEnabled(dialect, providerName)));
        }
    }
}
