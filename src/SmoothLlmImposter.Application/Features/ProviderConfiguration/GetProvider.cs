using FluentValidation;
using Mediator;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.Features.ProviderConfiguration;

public static class GetProvider
{
    public sealed record Request(string Key) : IRequest<ProviderConfigurationResponse?>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator() => ProviderConfigurationValidation.AddProviderKeyRules(RuleFor(x => x.Key));
    }

    public sealed class Handler(IProviderRegistry registry) : IRequestHandler<Request, ProviderConfigurationResponse?>
    {
        public ValueTask<ProviderConfigurationResponse?> Handle(Request request, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(
                registry.TryGet(request.Key, out ProviderOptions? provider)
                    ? ProviderConfigurationResponse.From(request.Key, provider)
                    : null);
        }
    }
}
