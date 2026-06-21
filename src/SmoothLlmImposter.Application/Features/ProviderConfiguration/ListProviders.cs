using FluentValidation;
using Mediator;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.Features.ProviderConfiguration;

public static class ListProviders
{
    public sealed record Request : IRequest<IReadOnlyList<ProviderConfigurationResponse>>;

    public sealed class Validator : AbstractValidator<Request>;

    public sealed class Handler(IProviderRegistry registry) : IRequestHandler<Request, IReadOnlyList<ProviderConfigurationResponse>>
    {
        public ValueTask<IReadOnlyList<ProviderConfigurationResponse>> Handle(Request request, CancellationToken cancellationToken)
        {
            IReadOnlyList<ProviderConfigurationResponse> providers = registry.Snapshot()
                .Select(static entry => ProviderConfigurationResponse.From(entry.Key, entry.Value))
                .ToArray();

            return ValueTask.FromResult(providers);
        }
    }
}
