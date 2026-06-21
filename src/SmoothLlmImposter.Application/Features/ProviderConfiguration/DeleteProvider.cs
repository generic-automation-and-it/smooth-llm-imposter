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
            if (!registry.TryGet(request.Key, out _))
            {
                return ValueTask.FromResult(false);
            }

            // Validate the post-delete registry before committing, symmetrically with Upsert/SetProviderEnabled,
            // so a delete can't leave the proxy in a state the startup validator would reject (e.g. zero
            // providers). The full set reseeds from config on restart (NFR-04).
            Dictionary<string, ProviderOptions> proposed = ProviderOptionsCloner.CloneDictionary(registry.Snapshot());
            proposed.Remove(request.Key);
            ProviderConfigurationValidation.EnsureValidRegistry(proposed);

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
