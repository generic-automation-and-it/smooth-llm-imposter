using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Credentials;

public static class CreateCredential
{
    public sealed record Request(
        string ProviderDialect,
        string Name,
        string Secret,
        string AuthScheme,
        string? BaseUrlOverride,
        string? AnthropicVersion,
        string? Actor) : IRequest<CredentialMutationResponse>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.ProviderDialect).Must(x => ApiDialectParser.TryParse(x, out _)).WithMessage("ProviderDialect must be 'openai' or 'anthropic'.");
            RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
            RuleFor(x => x.Secret).NotEmpty();
            RuleFor(x => x.AuthScheme).Must(x => Enum.TryParse<CredentialAuthScheme>(x, true, out _)).WithMessage("AuthScheme must be 'ApiKey' or 'Bearer'.");
            RuleFor(x => x.BaseUrlOverride).Must(BeAbsoluteUrlRoot).When(x => !string.IsNullOrWhiteSpace(x.BaseUrlOverride));
        }

        private static bool BeAbsoluteUrlRoot(string? value) =>
            Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }

    public sealed class Handler(
        ICredentialStore store,
        ISecretProtector secretProtector,
        ILogger<Handler> logger) : IRequestHandler<Request, CredentialMutationResponse>
    {
        public async ValueTask<CredentialMutationResponse> Handle(Request request, CancellationToken cancellationToken)
        {
            ApiDialect dialect = CredentialRequestHelpers.ParseDialect(request.ProviderDialect);
            CredentialAuthScheme scheme = CredentialRequestHelpers.ParseAuthScheme(request.AuthScheme);
            string ciphertext = secretProtector.Protect(request.Secret);
            ProviderCredential credential = CredentialRequestHelpers.NewCredential(
                dialect,
                request.Name,
                ciphertext,
                scheme,
                request.BaseUrlOverride,
                request.AnthropicVersion);

            ProviderCredential created = await store.AddAsync(credential, cancellationToken);
            logger.LogInformation(
                "Credential created by {Actor}: {CredentialName} ({ProviderDialect})",
                request.Actor ?? "unknown",
                created.Name,
                created.ProviderDialect);

            return new CredentialMutationResponse(CredentialResponse.From(created));
        }
    }
}
