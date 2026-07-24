using Microsoft.Extensions.Options;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Fail-fast validation of <see cref="ImposterOptions"/> at startup (wired via <c>ValidateOnStart</c>):
/// providers are keyed by name, so the dictionary key is the identity; each provider needs a known
/// dialect, an absolute base URL, and well-formed mappings, each dialect may declare at most one default
/// provider, and a legacy array shape (which binds as numeric keys) is rejected with migration guidance.
/// </summary>
internal sealed class ImposterOptionsValidator : IValidateOptions<ImposterOptions>
{
    public ValidateOptionsResult Validate(string? name, ImposterOptions options)
    {
        List<string> failures = [];

        if (options.Providers.Count == 0)
        {
            failures.Add("Imposter:Providers must contain at least one provider.");
        }

        // Legacy-array guard (HLD 007 / NFR-02): a JSON array binds into a Dictionary<string,T> as keys
        // "0","1",… . Purely numeric keys therefore mean an un-migrated array config — fail fast with the
        // new shape, and short-circuit so the per-provider checks don't spew noise about index keys.
        if (options.Providers.Keys.Any(static key => key.Length > 0 && key.All(char.IsAsciiDigit)))
        {
            failures.Add(
                "Imposter:Providers must be a name-keyed object, not an array. Numeric keys indicate a " +
                "legacy array config; use Providers: { \"<name>\": { ... } } " +
                "(e.g. \"opencode-go\": { \"Dialect\": \"openai\", \"BaseUrl\": \"https://...\" }).");
            return ValidateOptionsResult.Fail(failures);
        }

        // Effective name = explicit Name override, else the key. Uniqueness is case-insensitive, which
        // also catches case-only-duplicate keys (opencode-go vs OpenCode-Go) that the ordinal-keyed
        // dictionary keeps distinct (NFR-02).
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaultsByDialect = new Dictionary<ApiDialect, int>();

        foreach ((string key, ProviderOptions provider) in options.Providers)
        {
            string prefix = $"Imposter:Providers:{key}";

            // Name is an optional override of the key. null = omitted (use the key); a present-but-blank
            // value is an error — omit it rather than blanking it.
            if (provider.Name is not null && string.IsNullOrWhiteSpace(provider.Name))
            {
                failures.Add($"{prefix}:Name override is blank; omit Name to use the key '{key}' as the provider name, or set a non-blank value.");
            }

            string effectiveName = string.IsNullOrWhiteSpace(provider.Name) ? key : provider.Name;
            if (!seenNames.Add(effectiveName))
            {
                failures.Add($"{prefix}: provider name '{effectiveName}' is duplicated (provider keys and names must be unique, case-insensitively).");
            }

            if (!ApiDialectParser.TryParse(provider.Dialect, out ApiDialect dialect))
            {
                failures.Add($"{prefix}:Dialect '{provider.Dialect}' is invalid (expected 'openai' or 'anthropic').");
            }
            else if (provider.Enabled && provider.IsDefault)
            {
                defaultsByDialect[dialect] = defaultsByDialect.GetValueOrDefault(dialect) + 1;
            }

            if (string.IsNullOrWhiteSpace(provider.BaseUrl) ||
                !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out _))
            {
                failures.Add($"{prefix}:BaseUrl '{provider.BaseUrl}' is not an absolute URL.");
            }

            if (!OpenAiUpstreamApiParser.TryParse(provider.OpenAiUpstreamApi, out _))
            {
                failures.Add($"{prefix}:OpenAiUpstreamApi '{provider.OpenAiUpstreamApi}' is invalid (expected 'responses' or 'chat_completions').");
            }

            if (!CredentialAuthSchemeParser.TryParse(provider.AuthScheme, out _))
            {
                failures.Add($"{prefix}:AuthScheme '{provider.AuthScheme}' is invalid (expected 'ApiKey' or 'Bearer').");
            }

            // AuthHeader is an optional request-header-name override. null = omitted (use the scheme's default);
            // present-but-blank, malformed, and transport-owned names are errors.
            if (!AuthHeaderNameValidator.IsValid(provider.AuthHeader))
            {
                failures.Add(AuthHeaderNameValidator.FailureMessage(prefix));
            }

            if (!RequestNormalizationParser.TryParse(provider.RequestNormalization, out RequestNormalization normalization))
            {
                failures.Add($"{prefix}:RequestNormalization '{provider.RequestNormalization}' is invalid (expected 'none' or 'codex_to_openai_sdk').");
            }
            else if (normalization == RequestNormalization.CodexToOpenAiSdk)
            {
                // codex_to_openai_sdk targets the OpenAI Chat Completions tool contract; on a responses
                // upstream it would strip valid Responses tool types, and it is meaningless on anthropic.
                // (chat_completions enables it by default, so an explicit value is only needed to opt OUT.)
                if (!OpenAiUpstreamApiParser.TryParse(provider.OpenAiUpstreamApi, out OpenAiUpstreamApi upstreamApi) ||
                    upstreamApi != OpenAiUpstreamApi.ChatCompletions)
                {
                    failures.Add($"{prefix}:RequestNormalization 'codex_to_openai_sdk' requires OpenAiUpstreamApi 'chat_completions'.");
                }

                if (ApiDialectParser.TryParse(provider.Dialect, out ApiDialect normalizationDialect) &&
                    normalizationDialect != ApiDialect.OpenAi)
                {
                    failures.Add($"{prefix}:RequestNormalization 'codex_to_openai_sdk' is only valid on the 'openai' dialect.");
                }
            }

            if (!SessionForwardingParser.TryParse(provider.SessionForwarding, out _))
            {
                failures.Add($"{prefix}:SessionForwarding '{provider.SessionForwarding}' is invalid (expected 'none' or 'opencode-go'; also accepted: 'opencode_go', 'opencodego').");
            }

            for (int j = 0; j < provider.Models.Count; j++)
            {
                ModelMappingOptions mapping = provider.Models[j];
                if (string.IsNullOrWhiteSpace(mapping.From))
                {
                    failures.Add($"{prefix}:Models[{j}]:From is required.");
                }

                if (string.IsNullOrWhiteSpace(mapping.To))
                {
                    failures.Add($"{prefix}:Models[{j}]:To is required.");
                }
            }
        }

        foreach ((ApiDialect dialect, int count) in defaultsByDialect)
        {
            if (count > 1)
            {
                failures.Add($"Dialect '{dialect}' has {count} default providers; at most one is allowed.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
