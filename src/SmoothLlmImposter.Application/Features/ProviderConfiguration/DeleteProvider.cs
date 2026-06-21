using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.Features.ProviderConfiguration;

public static class DeleteProvider
{
    public sealed record Request(string Key, string? Actor) : IRequest<bool>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator() => ProviderConfigurationValidation.AddProviderKeyRules(RuleFor(x => x.Key));
    }

    public sealed class Handler(
        IProviderRegistry registry,
        ILogger<Handler> logger) : IRequestHandler<Request, bool>
    {
        public ValueTask<bool> Handle(Request request, CancellationToken cancellationToken)
        {
            bool deleted = registry.Delete(request.Key);
            if (deleted)
            {
                logger.LogInformation(
                    "Provider configuration deleted by {Actor}: {ProviderKey}",
                    request.Actor ?? "unknown",
                    request.Key);
            }

            return ValueTask.FromResult(deleted);
        }
    }
}
