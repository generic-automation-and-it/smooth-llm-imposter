using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.GithubCopilot;

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
        GithubCopilotProviderOwnership ownership,
        ILogger<Handler> logger) : IRequestHandler<Request, bool>
    {
        public async ValueTask<bool> Handle(Request request, CancellationToken cancellationToken)
        {
            if (!registry.TryGet(request.Key, out _))
            {
                return false;
            }

            if (GithubCopilotProviderOwnership.TryGetProfile(request.Key, out GithubCopilotProfile profile))
            {
                ownership.EnsureFeatureEnabled();

                // D1: any durable connection row — even one whose credentials are expired or need re-login —
                // reserves both keys. The row-existence check runs inside the same per-prefix gate the login
                // commit/publication holds, so "no row" cannot become stale between check and delete.
                using (await ownership.AcquireAsync(profile.Prefix, cancellationToken))
                {
                    if (ownership.HasDurableConnection(profile.Prefix))
                    {
                        throw new GithubCopilotProviderConflictException(
                            $"Provider '{request.Key}' is in use by the saved GitHub Copilot connection for prefix '{profile.Prefix}'. " +
                            "Deleting a provider is not a logout; remove the dedicated GitHub Copilot volume to delete saved credentials.");
                    }

                    return DeleteCore(request, logger);
                }
            }

            return DeleteCore(request, logger);
        }

        private bool DeleteCore(Request request, ILogger<Handler> logger)
        {
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

            return deleted;
        }
    }
}
