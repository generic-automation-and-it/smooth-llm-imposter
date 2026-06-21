using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.Features.ProviderConfiguration;

public static class UpsertProvider
{
    public sealed record Request(string Key, ProviderConfigurationBody Body, string? Actor) : IRequest<ProviderConfigurationResponse>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            ProviderConfigurationValidation.AddProviderKeyRules(RuleFor(x => x.Key));
            ProviderConfigurationValidation.AddBodyRules(RuleFor(x => x.Body));
        }
    }

    public sealed class Handler(
        IProviderRegistry registry,
        ILogger<Handler> logger) : IRequestHandler<Request, ProviderConfigurationResponse>
    {
        public ValueTask<ProviderConfigurationResponse> Handle(Request request, CancellationToken cancellationToken)
        {
            string? existingSecret = registry.TryGet(request.Key, out ProviderOptions existing)
                ? existing.Secret
                : null;
            ProviderOptions replacement = request.Body.ToProviderOptions(existingSecret);

            Dictionary<string, ProviderOptions> proposed = ProviderOptionsCloner.CloneDictionary(registry.Snapshot());
            proposed[request.Key] = replacement;
            ProviderConfigurationValidation.EnsureValidRegistry(proposed);

            registry.Upsert(request.Key, replacement);
            logger.LogInformation(
                "Provider configuration upserted by {Actor}: {ProviderKey}",
                request.Actor ?? "unknown",
                request.Key);

            return ValueTask.FromResult(ProviderConfigurationResponse.From(request.Key, replacement));
        }
    }
}
