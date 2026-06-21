using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Credentials;

public static class UpdateCredential
{
    public sealed record Request(
        Guid Id,
        string? ProviderName,
        string Name,
        string AuthScheme,
        string? Secret,
        string? BaseUrlOverride,
        string? AnthropicVersion,
        string? Actor) : IRequest<CredentialMutationResponse?>;

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Id).NotEmpty();
            RuleFor(x => x.ProviderName).MaximumLength(128);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
            RuleFor(x => x.AuthScheme).Must(x => Enum.TryParse<CredentialAuthScheme>(x, true, out _)).WithMessage("AuthScheme must be 'ApiKey' or 'Bearer'.");
            RuleFor(x => x.BaseUrlOverride).Must(BeAbsoluteUrlRoot).When(x => !string.IsNullOrWhiteSpace(x.BaseUrlOverride));
        }

        private static bool BeAbsoluteUrlRoot(string? value) =>
            Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }

    public sealed class Handler(
        ICredentialStore store,
        IProviderCatalog catalog,
        ISecretProtector secretProtector,
        ILogger<Handler> logger) : IRequestHandler<Request, CredentialMutationResponse?>
    {
        public async ValueTask<CredentialMutationResponse?> Handle(Request request, CancellationToken cancellationToken)
        {
            ProviderCredential? credential = await store.GetAsync(request.Id, cancellationToken);
            if (credential is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(request.ProviderName) &&
                !string.Equals(credential.ProviderName, request.ProviderName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                ApiDialect dialect = CredentialRequestHelpers.ParseDialect(credential.ProviderDialect);
                ProviderRoute provider = ProviderAddressResolver.Resolve(catalog, dialect, request.ProviderName, nameof(request.ProviderName));
                credential.MoveToProvider(provider.CredentialProviderName);
                credential.Deactivate();
            }

            credential.UpdateMetadata(
                request.Name,
                CredentialRequestHelpers.ParseAuthScheme(request.AuthScheme),
                request.BaseUrlOverride);

            if (credential is AnthropicCredential anthropic)
            {
                anthropic.SetAnthropicVersion(request.AnthropicVersion);
            }

            if (!string.IsNullOrWhiteSpace(request.Secret))
            {
                credential.RotateSecret(secretProtector.Protect(request.Secret));
            }

            ProviderCredential updated = await store.UpdateAsync(credential, cancellationToken);
            logger.LogInformation(
                "Credential updated by {Actor}: {CredentialName} ({ProviderDialect}/{ProviderName})",
                request.Actor ?? "unknown",
                updated.Name,
                updated.ProviderDialect,
                updated.ProviderName);

            return new CredentialMutationResponse(CredentialResponse.From(updated));
        }
    }
}
