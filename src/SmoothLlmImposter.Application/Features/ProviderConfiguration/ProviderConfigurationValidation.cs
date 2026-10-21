using FluentValidation;
using FluentValidation.Results;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.GithubCopilot;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.ProviderConfiguration;

internal static class ProviderConfigurationValidation
{
    public static void AddProviderKeyRules<T>(IRuleBuilderInitial<T, string> rule) =>
        rule.NotEmpty().MaximumLength(128);

    public static void AddBodyRules<T>(IRuleBuilderInitial<T, ProviderConfigurationBody> rule)
    {
        rule.NotNull().SetValidator(new ProviderConfigurationBodyValidator());
    }

    /// <summary>
    /// Field-level identity check for an <b>existing</b> reserved Copilot key (D1). Models and Enabled stay
    /// editable, but the feature owns dialect, the managed Bearer scheme, the header, the effective origin and
    /// the protocol surface — so a generic PUT can neither repoint the credential nor turn the profile into a
    /// Chat/normalized provider.
    /// </summary>
    public static void EnsureReservedIdentity(ProviderOptions existing, ProviderConfigurationBody body)
    {
        List<ValidationFailure> failures = [];

        if (!ApiDialectParser.TryParse(body.Dialect, out ApiDialect dialect))
        {
            failures.Add(new ValidationFailure(nameof(body.Dialect), "Dialect must be 'openai' or 'anthropic'."));
        }
        else if (!ApiDialectParser.TryParse(existing.Dialect, out ApiDialect existingDialect) || dialect != existingDialect)
        {
            failures.Add(new ValidationFailure(nameof(body.Dialect),
                $"Dialect is fixed by the reserved provider key ('{existingDialect.ToToken()}'); it cannot be changed."));
        }

        if (!CredentialAuthSchemeParser.TryParse(body.AuthScheme, out CredentialAuthScheme? scheme) ||
            scheme != CredentialAuthScheme.Bearer)
        {
            failures.Add(new ValidationFailure(nameof(body.AuthScheme),
                "AuthScheme is fixed to 'Bearer' for reserved GitHub Copilot providers (the managed connection supplies the token)."));
        }

        if (body.AuthHeader is not null)
        {
            failures.Add(new ValidationFailure(nameof(body.AuthHeader),
                "AuthHeader is fixed to the scheme default (Authorization) for reserved GitHub Copilot providers."));
        }

        if (!string.Equals(body.BaseUrl?.TrimEnd('/'), existing.BaseUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(new ValidationFailure(nameof(body.BaseUrl),
                "BaseUrl is managed by the GitHub Copilot connection origin; it cannot be changed through provider CRUD."));
        }

        if (body.IsDefault)
        {
            failures.Add(new ValidationFailure(nameof(body.IsDefault),
                "A reserved GitHub Copilot provider cannot be the dialect default; omitted-prefix pairs never replace passthrough routing."));
        }

        if (!OpenAiUpstreamApiParser.TryParse(body.OpenAiUpstreamApi, out OpenAiUpstreamApi upstreamApi) ||
            upstreamApi != OpenAiUpstreamApi.Responses)
        {
            failures.Add(new ValidationFailure(nameof(body.OpenAiUpstreamApi),
                "OpenAiUpstreamApi is fixed to 'responses' for reserved GitHub Copilot providers; Copilot Chat is out of scope."));
        }

        if (!RequestNormalizationParser.TryParse(body.RequestNormalization, out RequestNormalization normalization) ||
            normalization != RequestNormalization.None)
        {
            failures.Add(new ValidationFailure(nameof(body.RequestNormalization),
                "RequestNormalization is fixed to 'none' for reserved GitHub Copilot providers."));
        }

        if (body.StripEncryptedContent == true)
        {
            failures.Add(new ValidationFailure(nameof(body.StripEncryptedContent),
                "StripEncryptedContent cannot be enabled for reserved GitHub Copilot providers."));
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }

    public static void EnsureValidRegistry(IReadOnlyDictionary<string, ProviderOptions> providers)
    {
        var result = new ImposterOptionsValidator().Validate(null, new ImposterOptions
        {
            Providers = ProviderOptionsCloner.CloneDictionary(providers)
        });

        if (!result.Succeeded)
        {
            IEnumerable<string> failures = result.Failures ?? [result.FailureMessage ?? "Provider configuration is invalid."];
            throw new ValidationException(failures.Select(static failure => new ValidationFailure("Providers", failure)));
        }
    }

    private sealed class ProviderConfigurationBodyValidator : AbstractValidator<ProviderConfigurationBody>
    {
        public ProviderConfigurationBodyValidator()
        {
            RuleFor(x => x.Name).MaximumLength(128);
            RuleFor(x => x.Dialect).Must(static x => ApiDialectParser.TryParse(x, out _)).WithMessage("Dialect must be 'openai' or 'anthropic'.");
            RuleFor(x => x.BaseUrl).Must(BeAbsoluteUrlRoot).WithMessage("BaseUrl must be an absolute URL without query or fragment.");
            RuleFor(x => x.AuthScheme).Must(static x => CredentialAuthSchemeParser.TryParse(x, out _)).WithMessage("AuthScheme must be 'ApiKey' or 'Bearer'.");
            RuleFor(x => x.AuthHeader)
                .Must(AuthHeaderNameValidator.IsValid)
                .WithMessage("AuthHeader must be omitted or a custom request-header name; transport-owned headers (Content-*, Host, Transfer-Encoding) are not allowed.");
            RuleFor(x => x.OpenAiUpstreamApi).Must(static x => OpenAiUpstreamApiParser.TryParse(x, out _)).WithMessage("OpenAiUpstreamApi must be 'responses' or 'chat_completions'.");
            RuleFor(x => x.RequestNormalization).Must(static x => RequestNormalizationParser.TryParse(x, out _)).WithMessage("RequestNormalization must be 'none' or 'codex_to_openai_sdk'.");
            RuleFor(x => x.SessionForwarding).Must(static x => SessionForwardingParser.TryParse(x, out _)).WithMessage("SessionForwarding must be 'none' or 'opencode-go' (also accepted: 'opencode_go', 'opencodego').");
            // Same 1-3600 range the startup validator enforces (HLD 012 NFR-01). EnsureValidRegistry would
            // also catch it, but as a whole-registry failure — this gives the caller the offending field.
            RuleFor(x => x.TimeoutSeconds)
                .InclusiveBetween(1, 3600)
                .When(static x => x.TimeoutSeconds.HasValue)
                .WithMessage("TimeoutSeconds must be between 1 and 3600 seconds, or omitted for the default.");
            RuleFor(x => x.Models).NotNull();
            RuleForEach(x => x.Models).SetValidator(new ProviderModelMappingBodyValidator());
            RuleFor(x => x).Custom(static (body, context) =>
            {
                if (!RequestNormalizationParser.TryParse(body.RequestNormalization, out RequestNormalization normalization) ||
                    normalization != RequestNormalization.CodexToOpenAiSdk)
                {
                    return;
                }

                if (!OpenAiUpstreamApiParser.TryParse(body.OpenAiUpstreamApi, out OpenAiUpstreamApi upstreamApi) ||
                    upstreamApi != OpenAiUpstreamApi.ChatCompletions)
                {
                    context.AddFailure(nameof(body.RequestNormalization), "RequestNormalization 'codex_to_openai_sdk' requires OpenAiUpstreamApi 'chat_completions'.");
                }

                if (ApiDialectParser.TryParse(body.Dialect, out ApiDialect dialect) && dialect != ApiDialect.OpenAi)
                {
                    context.AddFailure(nameof(body.RequestNormalization), "RequestNormalization 'codex_to_openai_sdk' is only valid on the 'openai' dialect.");
                }
            });
        }

        private static bool BeAbsoluteUrlRoot(string? value) =>
            Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment);
    }

    private sealed class ProviderModelMappingBodyValidator : AbstractValidator<ProviderModelMappingBody>
    {
        public ProviderModelMappingBodyValidator()
        {
            RuleFor(x => x.From).NotEmpty();
            RuleFor(x => x.To).NotEmpty();
        }
    }
}
