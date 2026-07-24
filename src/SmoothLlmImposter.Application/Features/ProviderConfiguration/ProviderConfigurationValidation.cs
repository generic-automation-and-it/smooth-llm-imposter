using FluentValidation;
using FluentValidation.Results;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Credentials;
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
