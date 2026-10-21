using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.GithubCopilot;

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
        GithubCopilotProviderOwnership ownership,
        ILogger<Handler> logger) : IRequestHandler<Request, ProviderConfigurationResponse?>
    {
        public async ValueTask<ProviderConfigurationResponse?> Handle(Request request, CancellationToken cancellationToken)
        {
            // D1/D2: Enabled stays operator-editable for a reserved key, but the mutation is feature-owned and
            // shares the per-prefix gate with login commit/publication so the two cannot lose one another.
            if (GithubCopilotProviderOwnership.TryGetProfile(request.Key, out GithubCopilotProfile profile))
            {
                ownership.EnsureFeatureEnabled();
                using (await ownership.AcquireAsync(profile.Prefix, cancellationToken))
                {
                    return await Apply(request);
                }
            }

            return await Apply(request);
        }

        private ValueTask<ProviderConfigurationResponse?> Apply(Request request)
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
