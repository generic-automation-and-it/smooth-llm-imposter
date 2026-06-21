using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.Features.ProviderConfiguration;

public static class SetProviderEnabled
{
    public sealed record Request(string Key, bool Enabled, string? Actor) : IRequest<ProviderConfigurationResponse?>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator() => ProviderConfigurationValidation.AddProviderKeyRules(RuleFor(x => x.Key));
    }

    public sealed class Handler(
        IProviderRegistry registry,
        ILogger<Handler> logger) : IRequestHandler<Request, ProviderConfigurationResponse?>
    {
        public ValueTask<ProviderConfigurationResponse?> Handle(Request request, CancellationToken cancellationToken)
        {
            if (!registry.TryGet(request.Key, out ProviderOptions? existing))
            {
                return ValueTask.FromResult<ProviderConfigurationResponse?>(null);
            }

            ProviderOptions updated = ProviderOptionsCloner.Clone(existing);
            updated.Enabled = request.Enabled;

            Dictionary<string, ProviderOptions> proposed = ProviderOptionsCloner.CloneDictionary(registry.Snapshot());
            proposed[request.Key] = updated;
            ProviderConfigurationValidation.EnsureValidRegistry(proposed);

            registry.Upsert(request.Key, updated);
            logger.LogInformation(
                "Provider configuration {ProviderStatus} by {Actor}: {ProviderKey}",
                request.Enabled ? "enabled" : "disabled",
                request.Actor ?? "unknown",
                request.Key);

            return ValueTask.FromResult<ProviderConfigurationResponse?>(ProviderConfigurationResponse.From(request.Key, updated));
        }
    }
}
