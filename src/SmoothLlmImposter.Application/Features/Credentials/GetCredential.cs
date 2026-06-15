using FluentValidation;
using Mediator;
using SmoothLlmImposter.Application.Common.Persistence;

namespace SmoothLlmImposter.Application.Features.Credentials;

public static class GetCredential
{
    public sealed record Request(Guid Id) : IRequest<CredentialResponse?>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator() => RuleFor(x => x.Id).NotEmpty();
    }

    public sealed class Handler(ICredentialStore store) : IRequestHandler<Request, CredentialResponse?>
    {
        public async ValueTask<CredentialResponse?> Handle(Request request, CancellationToken cancellationToken)
        {
            return (await store.GetAsync(request.Id, cancellationToken)) is { } credential
                ? CredentialResponse.From(credential)
                : null;
        }
    }
}
