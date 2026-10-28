using Microsoft.Extensions.Options;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.GithubCopilot;
using SmoothLlmImposter.Domain.Routing;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Fail-fast validation of <see cref="ImposterOptions"/> at startup (wired via <c>ValidateOnStart</c>):
/// providers are keyed by name, so the dictionary key is the identity; each provider needs a known
/// dialect, an absolute base URL, and well-formed mappings, each dialect may declare at most one default
/// provider, and a legacy array shape (which binds as numeric keys) is rejected with migration guidance.
/// </summary>
internal sealed class ImposterOptionsValidator : IValidateOptions<ImposterOptions>
{
    private const int MinTimeoutSeconds = 1;
    private const int MaxTimeoutSeconds = 3600;

    public ValidateOptionsResult Validate(string? name, ImposterOptions options)
    {
        List<string> failures = [];

        AddDecisionsToolsFailures(options.DecisionsTools, options.ToolOrdering, failures);
        AddDecisionsMessagesFailures(options.DecisionsMessages, failures);
        AddLlmMessagesFailures(options.LlmMessages, failures);

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

            AddAnthropicChatBridgeFailures(prefix, provider, failures);

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

            // HLD 012: the base timeout is scaled ×1/×2/×3 across the three attempts, so the cap keeps the
            // worst-case header wait bounded. 0/negative would disable the header wait entirely, which the
            // retry handler cannot distinguish from a dead upstream.
            if (provider.TimeoutSeconds is int timeoutSeconds &&
                (timeoutSeconds < MinTimeoutSeconds || timeoutSeconds > MaxTimeoutSeconds))
            {
                failures.Add($"{prefix}:TimeoutSeconds '{timeoutSeconds}' is out of range (expected {MinTimeoutSeconds}-{MaxTimeoutSeconds} seconds, or omit it for the default).");
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

            // Reserved GitHub Copilot identities (R1/D1) are feature-owned: the suffix fixes the dialect, and
            // the managed connection supplies the token and origin. A config-seeded pair must satisfy the same
            // fixed identity a generic PUT is held to, or the host fails startup instead of routing oddly.
            if (GithubCopilotProfileKey.TryParse(key, out GithubCopilotProfile? profile))
            {
                AddReservedKeyFailures(prefix, provider, profile!, failures);
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

    private static void AddDecisionsToolsFailures(DecisionsToolsOptions options, ToolOrderingOptions toolOrdering, List<string> failures)
    {
        if (!options.Enabled)
        {
            return;
        }

        // Destination, path, model, attempts and timeout are the shared transport profile, validated by
        // DecisionServiceOptionsValidator whenever any consumer opts in (HLD 015 LADR-02).
        const string prefix = "Imposter:DecisionsTools";

        // LADR-13: sticky selection keeps the forwarded tool set but not its order, so without first-seen
        // ordering the tool prefix still changes whenever the client reorders its catalog.
        if (options.StickyPerSession && !toolOrdering.Enabled)
        {
            failures.Add(
                $"{prefix}:StickyPerSession (DECISIONS_TOOLS_STICKY_PER_SESSION) requires Imposter:ToolOrdering:Enabled " +
                "(Imposter__ToolOrdering__Enabled=true): sticky selection keeps the forwarded tool set but not its " +
                "order, so the tool prefix still changes when the client reorders its catalog. Set " +
                "Imposter__ToolOrdering__Enabled=true, or turn sticky selection off.");
        }

        if (!double.IsFinite(options.MinProbability) || options.MinProbability is < 0 or > 1)
        {
            failures.Add($"{prefix}:MinProbability must be a finite number between 0 and 1.");
        }

        if (options.MinKeepTools is < 0 or > 1000)
        {
            failures.Add($"{prefix}:MinKeepTools must be between 0 and 1000.");
        }

        if (options.CircuitBreakSeconds is < 1 or > 3600)
        {
            failures.Add($"{prefix}:CircuitBreakSeconds must be between 1 and 3600 seconds.");
        }
    }

    private static void AddDecisionsMessagesFailures(DecisionsMessagesOptions options, List<string> failures)
    {
        if (!options.Enabled)
        {
            return;
        }

        const string prefix = "Imposter:DecisionsMessages";

        if (!double.IsFinite(options.MinProbability) || options.MinProbability is < 0 or > 1)
        {
            failures.Add($"{prefix}:MinProbability must be a finite number between 0 and 1.");
        }

        if (options.CircuitBreakSeconds is < 1 or > 3600)
        {
            failures.Add($"{prefix}:CircuitBreakSeconds must be between 1 and 3600 seconds.");
        }

        if (options.MaxStateChars < 1)
        {
            failures.Add($"{prefix}:MaxStateChars must be at least 1.");
        }
    }

    private static void AddLlmMessagesFailures(LlmMessagesOptions options, List<string> failures)
    {
        if (!options.Enabled)
        {
            return;
        }

        const string prefix = "Imposter:LlmMessages";

        if (options.CacheTtlHours is < 1 or > 120)
        {
            failures.Add($"{prefix}:CacheTtlHours must be between 1 and 120.");
        }

        if (options.MinChars is < 0 or > 10_000)
        {
            failures.Add($"{prefix}:MinChars must be between 0 and 10000.");
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl) ||
            !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{prefix}:BaseUrl must be an absolute HTTP(S) URL when enabled.");
        }

        if (options.MaxAttempts is < 1 or > 10)
        {
            failures.Add($"{prefix}:MaxAttempts must be between 1 and 10.");
        }
        if (options.TimeoutSeconds is < 1 or > 3600)
        {
            failures.Add($"{prefix}:TimeoutSeconds must be between 1 and 3600 seconds.");
        }
        if (options.CircuitBreakSeconds is < 1 or > 3600)
        {
            failures.Add($"{prefix}:CircuitBreakSeconds must be between 1 and 3600 seconds.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            failures.Add($"{prefix}:Model is required when enabled.");
        }
    }

    private static void AddAnthropicChatBridgeFailures(string prefix, ProviderOptions provider, List<string> failures)
    {
        if (!AnthropicUpstreamApiParser.TryParse(provider.AnthropicUpstreamApi, out AnthropicUpstreamApi upstreamApi))
        {
            failures.Add($"{prefix}:AnthropicUpstreamApi '{provider.AnthropicUpstreamApi}' is invalid (expected 'messages' or 'chat_completions').");
            return;
        }

        if (!AnthropicChatCompatProfileParser.TryParse(provider.CompatProfile, out _))
        {
            failures.Add($"{prefix}:CompatProfile '{provider.CompatProfile}' is invalid (expected 'none', 'deepseek', 'minimax', 'mimo' or 'zhipu').");
        }

        if (!AnthropicChatUserIdModeParser.TryParse(provider.UserIdMode, out _))
        {
            failures.Add($"{prefix}:UserIdMode '{provider.UserIdMode}' is invalid (expected 'off', 'hash' or 'passthrough').");
        }

        if (!AnthropicChatMaxTokensFieldParser.TryParse(provider.MaxTokensField, out _))
        {
            failures.Add($"{prefix}:MaxTokensField '{provider.MaxTokensField}' is invalid (expected 'max_tokens' or 'max_completion_tokens').");
        }

        if (!AnthropicChatDocumentModeParser.TryParse(provider.DocumentMode, out _))
        {
            failures.Add($"{prefix}:DocumentMode '{provider.DocumentMode}' is invalid (expected 'placeholder' or 'image').");
        }

        if (!AnthropicChatMidturnSystemParser.TryParse(provider.MidturnSystem, out _))
        {
            failures.Add($"{prefix}:MidturnSystem '{provider.MidturnSystem}' is invalid (expected 'system' or 'merge_into_user').");
        }

        if (provider.MaxOutputTokens is int maxOutputTokens && maxOutputTokens < 16)
        {
            failures.Add($"{prefix}:MaxOutputTokens '{maxOutputTokens}' must be at least 16, or omitted.");
        }

        if (upstreamApi != AnthropicUpstreamApi.ChatCompletions)
        {
            return;
        }

        if (ApiDialectParser.TryParse(provider.Dialect, out ApiDialect dialect) && dialect != ApiDialect.Anthropic)
        {
            failures.Add($"{prefix}:AnthropicUpstreamApi 'chat_completions' is only valid on the 'anthropic' dialect.");
        }

        // The bridged forwarder writes a router-managed credential; a Chat-only upstream that speaks the
        // Anthropic ApiKey scheme (the dialect default) cannot authenticate that way. LADR-01 requires a
        // Bearer-capable scheme, so an omitted AuthScheme (which resolves to ApiKey) is also a failure.
        CredentialAuthScheme effectiveScheme;
        if (CredentialAuthSchemeParser.TryParse(provider.AuthScheme, out CredentialAuthScheme? scheme) && scheme is not null)
        {
            effectiveScheme = scheme.Value;
        }
        else
        {
            effectiveScheme = UpstreamAuthResolver.DefaultSchemeFor(dialect);
        }

        if (effectiveScheme != CredentialAuthScheme.Bearer)
        {
            failures.Add($"{prefix}:AnthropicUpstreamApi 'chat_completions' requires an effective AuthScheme of 'Bearer' (set AuthScheme to 'Bearer').");
        }
    }

    private static void AddReservedKeyFailures(
        string prefix,
        ProviderOptions provider,
        GithubCopilotProfile profile,
        List<string> failures)
    {
        if (!ApiDialectParser.TryParse(provider.Dialect, out ApiDialect dialect) || dialect != profile.Dialect)
        {
            failures.Add($"{prefix}:Dialect must be '{profile.Dialect.ToToken()}' for the reserved key '{profile.Key}'.");
        }

        if (!string.IsNullOrEmpty(provider.Secret))
        {
            failures.Add($"{prefix}:Secret must be omitted for a reserved GitHub Copilot provider; the managed connection supplies the token.");
        }

        if (!CredentialAuthSchemeParser.TryParse(provider.AuthScheme, out CredentialAuthScheme? scheme) ||
            scheme != CredentialAuthScheme.Bearer)
        {
            failures.Add($"{prefix}:AuthScheme must be 'Bearer' for a reserved GitHub Copilot provider.");
        }

        if (provider.AuthHeader is not null)
        {
            failures.Add($"{prefix}:AuthHeader must be omitted for a reserved GitHub Copilot provider (managed auth writes Authorization).");
        }

        if (provider.IsDefault)
        {
            failures.Add($"{prefix}:IsDefault must be false; a reserved GitHub Copilot provider is never the dialect default.");
        }

        if (!OpenAiUpstreamApiParser.TryParse(provider.OpenAiUpstreamApi, out OpenAiUpstreamApi upstreamApi) ||
            upstreamApi != OpenAiUpstreamApi.Responses)
        {
            failures.Add($"{prefix}:OpenAiUpstreamApi must be 'responses' for a reserved GitHub Copilot provider; Copilot Chat is out of scope.");
        }

        if (!RequestNormalizationParser.TryParse(provider.RequestNormalization, out RequestNormalization normalization) ||
            normalization != RequestNormalization.None)
        {
            failures.Add($"{prefix}:RequestNormalization must be 'none' for a reserved GitHub Copilot provider.");
        }

        if (provider.StripEncryptedContent == true)
        {
            failures.Add($"{prefix}:StripEncryptedContent cannot be enabled for a reserved GitHub Copilot provider.");
        }

        if (!AnthropicUpstreamApiParser.TryParse(provider.AnthropicUpstreamApi, out AnthropicUpstreamApi anthropicUpstreamApi) ||
            anthropicUpstreamApi != AnthropicUpstreamApi.Messages)
        {
            failures.Add($"{prefix}:AnthropicUpstreamApi must be 'messages' for a reserved GitHub Copilot provider; the Messages-to-Chat bridge is out of scope.");
        }

        if (!GithubCopilotTransportPolicy.IsAllowedOrigin(provider.BaseUrl))
        {
            failures.Add($"{prefix}:BaseUrl must be a GitHub-controlled HTTPS Copilot origin (e.g. https://api.githubcopilot.com).");
        }
    }
}
