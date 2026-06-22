using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Common.Persistence;

namespace SmoothLlmImposter.Application.Features.Credentials;

public static class ActivateCredential
{
    public sealed record Request(Guid Id, string? Actor) : IRequest<CredentialMutationResponse?>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator() => RuleFor(x => x.Id).NotEmpty();
    }

    public sealed class Handler(ICredentialStore store, ILogger<Handler> logger) : IRequestHandler<Request, CredentialMutationResponse?>
    {
        public async ValueTask<CredentialMutationResponse?> Handle(Request request, CancellationToken cancellationToken)
        {
            var credential = await store.GetAsync(request.Id, cancellationToken);
            if (credential is null)
            {
                return null;
            }

            var activated = await store.ActivateAsync(request.Id, cancellationToken);
            logger.LogInformation(
                "Credential activated by {Actor}: {CredentialName} ({ProviderDialect}/{ProviderName})",
                request.Actor ?? "unknown",
                activated.Name,
                activated.ProviderDialect,
                activated.ProviderName);

            return new CredentialMutationResponse(CredentialResponse.From(activated));
        }
    }
}
