using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Common.Persistence;

namespace SmoothLlmImposter.Application.Features.Credentials;

public static class DeleteCredential
{
    public sealed record Request(Guid Id, string? Actor) : IRequest<bool>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator() => RuleFor(x => x.Id).NotEmpty();
    }

    public sealed class Handler(ICredentialStore store, ILogger<Handler> logger) : IRequestHandler<Request, bool>
    {
        public async ValueTask<bool> Handle(Request request, CancellationToken cancellationToken)
        {
            var credential = await store.GetAsync(request.Id, cancellationToken);
            if (credential is null)
            {
                return false;
            }

            await store.DeleteAsync(request.Id, cancellationToken);
            logger.LogInformation(
                "Credential deleted by {Actor}: {CredentialName} ({ProviderDialect})",
                request.Actor ?? "unknown",
                credential.Name,
                credential.ProviderDialect);
            return true;
        }
    }
}
