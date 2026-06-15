using FluentValidation;
using Mediator;
using SmoothLlmImposter.Application.Common.Persistence;

namespace SmoothLlmImposter.Application.Features.Credentials;

public static class ListCredentials
{
    public sealed record Request : IRequest<IReadOnlyList<CredentialResponse>>;

    public sealed class Validator : AbstractValidator<Request>;

    public sealed class Handler(ICredentialStore store) : IRequestHandler<Request, IReadOnlyList<CredentialResponse>>
    {
        public async ValueTask<IReadOnlyList<CredentialResponse>> Handle(Request request, CancellationToken cancellationToken)
        {
            return (await store.ListAsync(cancellationToken)).Select(CredentialResponse.From).ToArray();
        }
    }
}
