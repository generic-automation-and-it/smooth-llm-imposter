using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.GithubCopilot;

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
        GithubCopilotProviderOwnership ownership,
        ILogger<Handler> logger) : IRequestHandler<Request, ProviderConfigurationResponse>
    {
        public async ValueTask<ProviderConfigurationResponse> Handle(Request request, CancellationToken cancellationToken)
        {
            if (GithubCopilotProviderOwnership.TryGetProfile(request.Key, out GithubCopilotProfile profile))
            {
                ownership.EnsureFeatureEnabled();

                // D1/D2: a reserved-key edit shares the per-prefix gate with login commit/publication, so an
                // operator's Models/Enabled edit and a publication cannot interleave read-then-write and lose
                // one another. The registry lock stays a short, non-awaited mutation inside Apply.
                using (await ownership.AcquireAsync(profile.Prefix, cancellationToken))
                {
                    return await Apply(request, profile);
                }
            }

            return await Apply(request, profile: null);
        }

        private ValueTask<ProviderConfigurationResponse> Apply(Request request, GithubCopilotProfile? profile)
        {
            bool exists = registry.TryGet(request.Key, out ProviderOptions? existing);

            // D1: reserved Copilot identities are feature-owned. Generic PUT may not create them, and an
            // existing key may only be edited in its Models/Enabled slots — identity stays fixed.
            if (profile is not null)
            {
                if (!exists)
                {
                    throw new GithubCopilotProviderConflictException(
                        $"Provider key '{request.Key}' is reserved for GitHub Copilot and cannot be created through provider CRUD. " +
                        $"Start a login for prefix '{profile.Prefix}' to materialize the pair.");
                }

                ProviderConfigurationValidation.EnsureReservedIdentity(existing!, request.Body);
            }

            string? existingSecret = exists ? existing!.Secret : null;
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
