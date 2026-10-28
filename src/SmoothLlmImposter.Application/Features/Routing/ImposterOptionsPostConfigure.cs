using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Layers the conventional per-provider environment surface (HLD 007, LADR-02/03) onto the bound
/// <see cref="ImposterOptions"/>. For a provider keyed <c>opencode-go</c> the env prefix is
/// <c>OPENCODE_GO_</c>, and conventional suffixes map onto the provider's scalar fields
/// (<c>OPENCODE_GO_API_KEY</c> → <see cref="ProviderOptions.Secret"/>, etc.). The secret is reachable via
/// three suffixes — <c>_API_KEY</c> (api-key-typed), and the Bearer-typed aliases
/// <c>_AUTHORIZATION_BEARER</c> (e.g. <c>ANTHROPIC_PERSONAL_AUTHORIZATION_BEARER</c> for a Bearer
/// subscription token) and <c>_AUTH_TOKEN</c> (mirroring the Claude Code / Anthropic SDK
/// <c>ANTHROPIC_AUTH_TOKEN</c> Bearer variable) — all filling <see cref="ProviderOptions.Secret"/>.
/// <b>Which suffix wins follows the provider's effective auth scheme</b> (naming-convention priority):
/// a <c>Bearer</c> provider prefers <c>_AUTH_TOKEN</c> → <c>_AUTHORIZATION_BEARER</c> → <c>_API_KEY</c>,
/// an <c>ApiKey</c> provider prefers <c>_API_KEY</c> → <c>_AUTH_TOKEN</c> → <c>_AUTHORIZATION_BEARER</c>.
/// The off-scheme suffixes remain as fallbacks so a single populated var still authenticates. This keeps
/// a personal <c>ANTHROPIC_API_KEY</c> from being sent as a Bearer token (and vice versa) when both a
/// key and a token are exported. The effective scheme is resolved from the conventional
/// <c>_AUTH_SCHEME</c> env override, then the bound <see cref="ProviderOptions.AuthScheme"/>, then the
/// dialect default (openai → Bearer, anthropic → ApiKey). Dialect-suffixed providers
/// always fall back to the base provider's secret convention — a lone
/// <c>openrouter-anthropic</c> (no <c>openrouter-openai</c> sibling, no base <c>openrouter</c>
/// provider) resolves <c>OPENROUTER_API_KEY</c> after its own <c>OPENROUTER_ANTHROPIC_API_KEY</c>,
/// and <c>openrouter-openai</c> + <c>openrouter-anthropic</c> share <c>OPENROUTER_API_KEY</c> — while
/// retaining their own bound <c>AuthScheme</c>. The fallback is no longer gated on a
/// sibling/base provider existing (LADR-02/03).
/// <para>
/// Runs as an <see cref="IPostConfigureOptions{TOptions}"/>, so it executes after the binder and
/// <b>before</b> <c>IValidateOptions</c> at <c>Get</c>/<c>ValidateOnStart</c> — a conventionally-supplied
/// <c>Dialect</c>/<c>BaseUrl</c> is therefore in place before validation. Precedence is fixed
/// (highest wins): conventional env &gt; structured env (<c>Imposter__Providers__&lt;name&gt;__Field</c>)
/// &gt; appsettings. The structured-vs-appsettings ordering is the binder's; this type only adds the
/// conventional layer on top by reading the conventional var directly from <see cref="IConfiguration"/>
/// (which already merges environment variables, case-insensitively) and applying it when present.
/// </para>
/// <para>
/// Inputs are <see cref="IConfiguration"/> and process environment only — no DB, no network, no persisted
/// state (NFR-04). A resolved secret <b>value</b> is never logged or surfaced; only the variable name +
/// provider + field are logged, at <c>Debug</c> (NFR-03).
/// </para>
/// </summary>
internal sealed class ImposterOptionsPostConfigure(
    IConfiguration configuration,
    ILogger<ImposterOptionsPostConfigure> logger) : IPostConfigureOptions<ImposterOptions>
{
    private static readonly string[] SharedProviderSecretSuffixes = ["-anthropic", "-openai"];

    /// <summary>
    /// The conventional suffix → field surface. <see cref="ConventionalField.PropertyName"/> names the
    /// <see cref="ProviderOptions"/> property each suffix targets; a guard test asserts this set covers
    /// every conventionally-overridable scalar, so a new scalar field can't silently lack a suffix
    /// (LADR-02 Open item). <c>Models[]</c> and the identity field <c>Name</c> are intentionally absent.
    /// </summary>
    internal static readonly IReadOnlyList<ConventionalField> Fields =
    [
        // Three suffixes fill the same Secret slot; the winner is chosen by the effective auth scheme
        // (see ResolveEffectiveScheme / ApplyConventionalSecret), not by their order here. _API_KEY is the
        // api-key-typed spelling; _AUTHORIZATION_BEARER and _AUTH_TOKEN are Bearer-typed. A Bearer
        // provider prefers the Bearer-typed vars; an ApiKey provider prefers _API_KEY.
        new("_API_KEY", nameof(ProviderOptions.Secret), static (p, v) => p.Secret = v),
        // Bearer-typed alias: a Bearer subscription token (e.g. from `claude setup-token` on a personal
        // provider) reads more naturally as <NAME>_AUTHORIZATION_BEARER than as <NAME>_API_KEY.
        new("_AUTHORIZATION_BEARER", nameof(ProviderOptions.Secret), static (p, v) => p.Secret = v),
        // Bearer-typed alias mirroring the Claude Code / Anthropic SDK <NAME>_AUTH_TOKEN Bearer-token
        // variable (e.g. ANTHROPIC_AUTH_TOKEN) so an operator can reuse the exact env var they export for cc.
        new("_AUTH_TOKEN", nameof(ProviderOptions.Secret), static (p, v) => p.Secret = v),
        new("_BASE_URL", nameof(ProviderOptions.BaseUrl), static (p, v) => p.BaseUrl = v),
        new("_AUTH_SCHEME", nameof(ProviderOptions.AuthScheme), static (p, v) => p.AuthScheme = v),
        // Overrides only the header NAME the credential is written into (value format follows _AUTH_SCHEME);
        // e.g. OPENAI_AUTH_HEADER=api-key for the MyCompany Gateway.
        new("_AUTH_HEADER", nameof(ProviderOptions.AuthHeader), static (p, v) => p.AuthHeader = v),
        new("_DIALECT", nameof(ProviderOptions.Dialect), static (p, v) => p.Dialect = v),
        // Booleans have no-op Apply delegates: PostConfigure handles them inline because applying them
        // requires bool.TryParse plus a Warning on an unparseable value, which
        // the Action<ProviderOptions, string> delegate (no logger) cannot do.
        new("_IS_DEFAULT", nameof(ProviderOptions.IsDefault), static (_, _) => { }),
        new("_ENABLED", nameof(ProviderOptions.Enabled), static (_, _) => { }),
        new("_OPENAI_UPSTREAM_API", nameof(ProviderOptions.OpenAiUpstreamApi), static (p, v) => p.OpenAiUpstreamApi = v),
        new("_REQUEST_NORMALIZATION", nameof(ProviderOptions.RequestNormalization), static (p, v) => p.RequestNormalization = v),
        new("_SESSION_FORWARDING", nameof(ProviderOptions.SessionForwarding), static (p, v) => p.SessionForwarding = v),
        new("_ANTHROPIC_VERSION", nameof(ProviderOptions.AnthropicVersion), static (p, v) => p.AnthropicVersion = v),
        new("_STRIP_ENCRYPTED_CONTENT", nameof(ProviderOptions.StripEncryptedContent), static (_, _) => { }),
        // HLD 017 per-route opt-out of message optimization; boolean, applied inline like the others.
        new("_OPTIMIZE_MESSAGES", nameof(ProviderOptions.OptimizeMessages), static (_, _) => { }),
        // Integer field: applied inline for the same reason as the booleans — parsing plus a Warning on an
        // unparseable value needs the logger the Apply delegate does not have.
        new("_TIMEOUT_SECONDS", nameof(ProviderOptions.TimeoutSeconds), static (_, _) => { }),
        // HLD 016 Messages-to-Chat bridge surface. String options carry their parser's vocabulary; the
        // boolean/integer options are applied inline for the same logger reason as above.
        new("_ANTHROPIC_UPSTREAM_API", nameof(ProviderOptions.AnthropicUpstreamApi), static (p, v) => p.AnthropicUpstreamApi = v),
        new("_COMPAT_PROFILE", nameof(ProviderOptions.CompatProfile), static (p, v) => p.CompatProfile = v),
        new("_SEND_REASONING_EFFORT", nameof(ProviderOptions.SendReasoningEffort), static (_, _) => { }),
        new("_USER_ID_MODE", nameof(ProviderOptions.UserIdMode), static (p, v) => p.UserIdMode = v),
        new("_MAX_TOKENS_FIELD", nameof(ProviderOptions.MaxTokensField), static (p, v) => p.MaxTokensField = v),
        new("_MAX_OUTPUT_TOKENS", nameof(ProviderOptions.MaxOutputTokens), static (_, _) => { }),
        new("_FORWARD_CACHE_CONTROL", nameof(ProviderOptions.ForwardCacheControl), static (_, _) => { }),
        new("_SEND_RESPONSE_FORMAT", nameof(ProviderOptions.SendResponseFormat), static (_, _) => { }),
        new("_DOCUMENT_MODE", nameof(ProviderOptions.DocumentMode), static (p, v) => p.DocumentMode = v),
        new("_THINK_MARKUP_EXTRACTION", nameof(ProviderOptions.ThinkMarkupExtraction), static (_, _) => { }),
        new("_MIDTURN_SYSTEM", nameof(ProviderOptions.MidturnSystem), static (p, v) => p.MidturnSystem = v),
    ];

    // Naming-convention priority for the Secret slot, keyed by effective auth scheme. The scheme-matching
    // suffix is tried first; the off-scheme suffixes stay as fallbacks so a single populated var still
    // authenticates. Every entry maps to a Secret-typed suffix in Fields.
    private static readonly string[] BearerSecretSuffixOrder = ["_AUTH_TOKEN", "_AUTHORIZATION_BEARER", "_API_KEY"];
    private static readonly string[] ApiKeySecretSuffixOrder = ["_API_KEY", "_AUTH_TOKEN", "_AUTHORIZATION_BEARER"];

    /// <summary>The Secret-typed <see cref="ConventionalField"/> entries indexed by their suffix, so the
    /// scheme-ordered secret resolution can reuse a single field definition (and its shared-provider
    /// fallback) per suffix.</summary>
    private static readonly IReadOnlyDictionary<string, ConventionalField> SecretFieldsBySuffix =
        Fields.Where(f => f.PropertyName == nameof(ProviderOptions.Secret))
              .ToDictionary(f => f.Suffix, StringComparer.Ordinal);

    public void PostConfigure(string? name, ImposterOptions options)
    {
        // Root-level boolean toggle (HLD 010). Same bool.TryParse + warn-on-invalid pattern as the
        // per-provider _IS_DEFAULT / _ENABLED fields below. A blank/whitespace value is treated as
        // absent so an empty-but-present var never blanks the appsettings default.
        ApplyRootBooleanOverride("IMPOSTER_WHO_MESSAGE_ENABLED", nameof(options.WhoMessage.Enabled), value =>
        {
            options.WhoMessage.Enabled = value;
        });

        ApplyDecisionsOverrides(options.Decisions);

        ApplyDecisionsToolsOverrides(options.DecisionsTools, options.Decisions);

        ApplyDecisionsMessagesOverrides(options.DecisionsMessages, options.Decisions);

        ApplyLlmMessagesOverrides(options.LlmMessages);

        // DECISIONS_MESSAGES_ENABLED unset (neither conventional env nor structured config) takes
        // LLM_MESSAGES_ENABLED value (LADR-06). Never overwrite an explicit structured/appsettings value.
        bool conventionalSet = !string.IsNullOrWhiteSpace(configuration["DECISIONS_MESSAGES_ENABLED"]);
        bool structuredSet = !string.IsNullOrWhiteSpace(configuration["Imposter:DecisionsMessages:Enabled"]);
        if (!conventionalSet && !structuredSet)
        {
            options.DecisionsMessages.Enabled = options.LlmMessages.Enabled;
        }

        // The gate has no effect while the optimizer is off, whatever its own value says (LADR-06).
        // Leaving it enabled would opt the message consumer into the shared decision demand.
        if (!options.LlmMessages.Enabled && options.DecisionsMessages.Enabled)
        {
            logger.LogInformation(
                "DECISIONS_MESSAGES_ENABLED is ignored while LLM_MESSAGES_ENABLED is off; the message gate is disabled.");
            options.DecisionsMessages.Enabled = false;
        }

        foreach ((string key, ProviderOptions provider) in options.Providers)
        {
            string prefix = ToEnvPrefix(key);
            if (prefix.Length == 0)
            {
                continue;
            }

            // Secret is reachable via three suffixes whose winner follows the effective auth scheme
            // (Bearer → _AUTH_TOKEN/_AUTHORIZATION_BEARER first; ApiKey → _API_KEY first). Resolved
            // separately so the loop below only handles the non-secret fields.
            ApplyConventionalSecret(key, provider, prefix);

            foreach (ConventionalField field in Fields)
            {
                // Secret suffixes are handled by ApplyConventionalSecret (scheme-driven priority).
                if (field.PropertyName == nameof(ProviderOptions.Secret))
                {
                    continue;
                }

                (string variable, string? value) = ResolveConventionalValue(key, field, prefix);

                // An empty/whitespace conventional value is treated as ABSENT, not as an override (e.g. a
                // compose `X=${X:-}` with the host var unset writes an empty-but-present var), so it never
                // blanks a value already bound from appsettings.
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (field.PropertyName is nameof(ProviderOptions.TimeoutSeconds)
                    or nameof(ProviderOptions.MaxOutputTokens))
                {
                    // Out-of-range is rejected by the validator, not here: silently dropping a typo'd
                    // 0/-5 would ship the default while the operator believes their value applies.
                    if (!int.TryParse(value, out int integerValue))
                    {
                        logger.LogWarning(
                            "Ignoring conventional override {EnvVar} for provider {Provider} field {Field}: value '{Value}' is not a whole number.",
                            variable, key, field.PropertyName, value);
                        continue;
                    }

                    if (field.PropertyName == nameof(ProviderOptions.TimeoutSeconds))
                    {
                        provider.TimeoutSeconds = integerValue;
                    }
                    else
                    {
                        provider.MaxOutputTokens = integerValue;
                    }

                    LogApplied(variable, key, field.PropertyName);
                    continue;
                }

                if (field.PropertyName is nameof(ProviderOptions.IsDefault)
                    or nameof(ProviderOptions.Enabled)
                    or nameof(ProviderOptions.StripEncryptedContent)
                    or nameof(ProviderOptions.OptimizeMessages)
                    or nameof(ProviderOptions.SendReasoningEffort)
                    or nameof(ProviderOptions.ForwardCacheControl)
                    or nameof(ProviderOptions.SendResponseFormat)
                    or nameof(ProviderOptions.ThinkMarkupExtraction))
                {
                    if (!bool.TryParse(value, out bool booleanValue))
                    {
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            logger.LogWarning(
                                "Ignoring conventional override {EnvVar} for provider {Provider} field {Field}: value '{Value}' is not a recognized boolean.",
                                variable, key, field.PropertyName, value);
                        }

                        continue;
                    }

                    switch (field.PropertyName)
                    {
                        case nameof(ProviderOptions.IsDefault):
                            provider.IsDefault = booleanValue;
                            break;
                        case nameof(ProviderOptions.Enabled):
                            provider.Enabled = booleanValue;
                            break;
                        case nameof(ProviderOptions.StripEncryptedContent):
                            provider.StripEncryptedContent = booleanValue;
                            break;
                        case nameof(ProviderOptions.OptimizeMessages):
                            provider.OptimizeMessages = booleanValue;
                            break;
                        case nameof(ProviderOptions.SendReasoningEffort):
                            provider.SendReasoningEffort = booleanValue;
                            break;
                        case nameof(ProviderOptions.ForwardCacheControl):
                            provider.ForwardCacheControl = booleanValue;
                            break;
                        case nameof(ProviderOptions.SendResponseFormat):
                            provider.SendResponseFormat = booleanValue;
                            break;
                        case nameof(ProviderOptions.ThinkMarkupExtraction):
                            provider.ThinkMarkupExtraction = booleanValue;
                            break;
                    }

                    LogApplied(variable, key, field.PropertyName);
                    continue;
                }

                field.Apply(provider, value);
                LogApplied(variable, key, field.PropertyName);
            }
        }
    }

    private void LogApplied(string variable, string key, string propertyName)
    {
        // NFR-03: log the variable name + provider + field, never the resolved value.
        logger.LogDebug(
            "Applied conventional override {EnvVar} to provider {Provider} field {Field}.",
            variable, key, propertyName);
    }

    /// <summary>
    /// Conventional variable per <see cref="DecisionsToolsOptions"/> property. The overrides below read their
    /// names from here, so the drift guard that reflects over the options type covers the applied surface.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> DecisionsToolsVariables =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(DecisionsToolsOptions.Enabled)] = "DECISIONS_TOOLS_ENABLED",
            [nameof(DecisionsToolsOptions.BaseUrl)] = "DECISIONS_TOOLS_BASE_URL",
            [nameof(DecisionsToolsOptions.Path)] = "DECISIONS_TOOLS_PATH",
            [nameof(DecisionsToolsOptions.Model)] = "DECISIONS_TOOLS_MODEL",
            [nameof(DecisionsToolsOptions.MinProbability)] = "DECISIONS_TOOLS_MIN_PROBABILITY",
            [nameof(DecisionsToolsOptions.MinKeepTools)] = "DECISIONS_TOOLS_MIN_KEEP",
            [nameof(DecisionsToolsOptions.AlwaysKeep)] = "DECISIONS_TOOLS_ALWAYS_KEEP",
            [nameof(DecisionsToolsOptions.StickyPerSession)] = "DECISIONS_TOOLS_STICKY_PER_SESSION",
            [nameof(DecisionsToolsOptions.MaxAttempts)] = "DECISIONS_TOOLS_MAX_ATTEMPTS",
            [nameof(DecisionsToolsOptions.TimeoutSeconds)] = "DECISIONS_TOOLS_TIMEOUT",
            [nameof(DecisionsToolsOptions.CircuitBreakSeconds)] = "DECISIONS_TOOLS_CIRCUIT_BREAK_SECONDS",
        };

    /// <summary>
    /// Conventional variable per <see cref="DecisionsOptions"/> property: the shared layer under every
    /// decision section (HLD 017 LADR-06). No <c>DECISIONS_ENABLED</c> exists by design.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> DecisionsVariables =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(DecisionsOptions.BaseUrl)] = "DECISIONS_BASE_URL",
            [nameof(DecisionsOptions.Path)] = "DECISIONS_PATH",
            [nameof(DecisionsOptions.Model)] = "DECISIONS_MODEL",
            [nameof(DecisionsOptions.MinProbability)] = "DECISIONS_MIN_PROBABILITY",
            [nameof(DecisionsOptions.MaxAttempts)] = "DECISIONS_MAX_ATTEMPTS",
            [nameof(DecisionsOptions.TimeoutSeconds)] = "DECISIONS_TIMEOUT",
            [nameof(DecisionsOptions.CircuitBreakSeconds)] = "DECISIONS_CIRCUIT_BREAK_SECONDS",
            [nameof(DecisionsOptions.MinKeepTools)] = "DECISIONS_MIN_KEEP",
            [nameof(DecisionsOptions.AlwaysKeep)] = "DECISIONS_ALWAYS_KEEP",
            [nameof(DecisionsOptions.StickyPerSession)] = "DECISIONS_STICKY_PER_SESSION",
        };

    private const string DecisionsToolsSection = ImposterOptions.SectionName + ":" + nameof(ImposterOptions.DecisionsTools);

    private const string DecisionsMessagesSection = ImposterOptions.SectionName + ":" + nameof(ImposterOptions.DecisionsMessages);

    private void ApplyDecisionsOverrides(DecisionsOptions options)
    {
        ApplyRootStringOverride(DecisionsVariables[nameof(options.BaseUrl)], nameof(options.BaseUrl), value => options.BaseUrl = value);
        ApplyRootStringOverride(DecisionsVariables[nameof(options.Path)], nameof(options.Path), value => options.Path = value);
        ApplyRootStringOverride(DecisionsVariables[nameof(options.Model)], nameof(options.Model), value => options.Model = value);
        ApplyRootProbabilityOverride(DecisionsVariables[nameof(options.MinProbability)], nameof(options.MinProbability), value => options.MinProbability = value);
        ApplyRootIntegerOverride(DecisionsVariables[nameof(options.MaxAttempts)], nameof(options.MaxAttempts), value => options.MaxAttempts = value);
        ApplyRootIntegerOverride(DecisionsVariables[nameof(options.TimeoutSeconds)], nameof(options.TimeoutSeconds), value => options.TimeoutSeconds = value);
        ApplyRootIntegerOverride(DecisionsVariables[nameof(options.CircuitBreakSeconds)], nameof(options.CircuitBreakSeconds), value => options.CircuitBreakSeconds = value);
        ApplyRootIntegerOverride(DecisionsVariables[nameof(options.MinKeepTools)], nameof(options.MinKeepTools), value => options.MinKeepTools = value);
        ApplyRootStringOverride(DecisionsVariables[nameof(options.AlwaysKeep)], nameof(options.AlwaysKeep), value => options.AlwaysKeep = value);
        ApplyRootBooleanOverride(DecisionsVariables[nameof(options.StickyPerSession)], nameof(options.StickyPerSession), value => options.StickyPerSession = value);
    }

    private void ApplyDecisionsToolsOverrides(DecisionsToolsOptions options, DecisionsOptions shared)
    {
        IReadOnlyDictionary<string, string> variables = DecisionsToolsVariables;
        const string section = DecisionsToolsSection;

        ApplyRootBooleanOverride(variables[nameof(options.Enabled)], nameof(options.Enabled), value => options.Enabled = value);
        LayerString(section, variables, nameof(options.BaseUrl), shared.BaseUrl, value => options.BaseUrl = value);
        LayerString(section, variables, nameof(options.Path), shared.Path, value => options.Path = value);
        LayerString(section, variables, nameof(options.Model), shared.Model, value => options.Model = value);
        LayerProbability(section, variables, nameof(options.MinProbability), shared.MinProbability, value => options.MinProbability = value);
        LayerInteger(section, variables, nameof(options.MinKeepTools), shared.MinKeepTools, value => options.MinKeepTools = value);
        LayerString(section, variables, nameof(options.AlwaysKeep), shared.AlwaysKeep, value => options.AlwaysKeep = value);
        LayerBoolean(section, variables, nameof(options.StickyPerSession), shared.StickyPerSession, value => options.StickyPerSession = value);
        LayerInteger(section, variables, nameof(options.MaxAttempts), shared.MaxAttempts, value => options.MaxAttempts = value);
        LayerInteger(section, variables, nameof(options.TimeoutSeconds), shared.TimeoutSeconds, value => options.TimeoutSeconds = value);
        LayerInteger(section, variables, nameof(options.CircuitBreakSeconds), shared.CircuitBreakSeconds, value => options.CircuitBreakSeconds = value);
    }

    private void LayerString(string section, IReadOnlyDictionary<string, string> variables, string propertyName, string? shared, Action<string> apply)
    {
        bool sectionApplied = ApplyRootStringOverride(variables[propertyName], propertyName, apply);
        if (!string.IsNullOrWhiteSpace(shared))
        {
            Layer(section, propertyName, sectionApplied, () => apply(shared));
        }
    }

    private void LayerInteger(string section, IReadOnlyDictionary<string, string> variables, string propertyName, int? shared, Action<int> apply)
    {
        bool sectionApplied = ApplyRootIntegerOverride(variables[propertyName], propertyName, apply);
        if (shared is { } value)
        {
            Layer(section, propertyName, sectionApplied, () => apply(value));
        }
    }

    private void LayerProbability(string section, IReadOnlyDictionary<string, string> variables, string propertyName, double? shared, Action<double> apply)
    {
        bool sectionApplied = ApplyRootProbabilityOverride(variables[propertyName], propertyName, apply);
        if (shared is { } value)
        {
            Layer(section, propertyName, sectionApplied, () => apply(value));
        }
    }

    private void LayerBoolean(string section, IReadOnlyDictionary<string, string> variables, string propertyName, bool? shared, Action<bool> apply)
    {
        bool sectionApplied = ApplyRootBooleanOverride(variables[propertyName], propertyName, apply);
        if (shared is { } value)
        {
            Layer(section, propertyName, sectionApplied, () => apply(value));
        }
    }

    /// <summary>
    /// The one precedence rule for every decision section field (HLD 017 LADR-06): a section value, from
    /// its conventional variable or its structured key, wins; otherwise a set shared <c>DECISIONS_*</c> value
    /// fills it; otherwise the field keeps its built-in default. A blank value counts as unset at every layer.
    /// </summary>
    private void Layer(string section, string propertyName, bool sectionApplied, Action applyShared)
    {
        if (sectionApplied || !string.IsNullOrWhiteSpace(configuration[$"{section}:{propertyName}"]))
        {
            return;
        }

        applyShared();
        logger.LogDebug("Applied shared decision setting to {Section} field {Field}.", section, propertyName);
    }

    /// <summary>
    /// Conventional variable per <see cref="DecisionsMessagesOptions"/> property (HLD 017).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> DecisionsMessagesVariables =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(DecisionsMessagesOptions.Enabled)] = "DECISIONS_MESSAGES_ENABLED",
            [nameof(DecisionsMessagesOptions.BaseUrl)] = "DECISIONS_MESSAGES_BASE_URL",
            [nameof(DecisionsMessagesOptions.Path)] = "DECISIONS_MESSAGES_PATH",
            [nameof(DecisionsMessagesOptions.Model)] = "DECISIONS_MESSAGES_MODEL",
            [nameof(DecisionsMessagesOptions.MinProbability)] = "DECISIONS_MESSAGES_MIN_PROBABILITY",
            [nameof(DecisionsMessagesOptions.MaxAttempts)] = "DECISIONS_MESSAGES_MAX_ATTEMPTS",
            [nameof(DecisionsMessagesOptions.TimeoutSeconds)] = "DECISIONS_MESSAGES_TIMEOUT",
            [nameof(DecisionsMessagesOptions.CircuitBreakSeconds)] = "DECISIONS_MESSAGES_CIRCUIT_BREAK_SECONDS",
        };

    internal static readonly IReadOnlyDictionary<string, string> LlmMessagesVariables =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(LlmMessagesOptions.Enabled)] = "LLM_MESSAGES_ENABLED",
            [nameof(LlmMessagesOptions.BaseUrl)] = "LLM_MESSAGES_BASE_URL",
            [nameof(LlmMessagesOptions.Path)] = "LLM_MESSAGES_PATH",
            [nameof(LlmMessagesOptions.Model)] = "LLM_MESSAGES_MODEL",
            [nameof(LlmMessagesOptions.MaxAttempts)] = "LLM_MESSAGES_MAX_ATTEMPTS",
            [nameof(LlmMessagesOptions.TimeoutSeconds)] = "LLM_MESSAGES_TIMEOUT",
            [nameof(LlmMessagesOptions.CircuitBreakSeconds)] = "LLM_MESSAGES_CIRCUIT_BREAK_SECONDS",
            [nameof(LlmMessagesOptions.CacheTtlHours)] = "LLM_MESSAGES_CACHE_TTL_HOURS",
            [nameof(LlmMessagesOptions.MinChars)] = "LLM_MESSAGES_MIN_CHARS",
            [nameof(LlmMessagesOptions.BudgetSeconds)] = "LLM_MESSAGES_BUDGET_SECONDS",
            [nameof(LlmMessagesOptions.ApiKey)] = "LLM_MESSAGES_API_KEY",
        };

    private void ApplyDecisionsMessagesOverrides(DecisionsMessagesOptions options, DecisionsOptions shared)
    {
        IReadOnlyDictionary<string, string> variables = DecisionsMessagesVariables;
        const string section = DecisionsMessagesSection;

        ApplyRootBooleanOverride(variables[nameof(options.Enabled)], nameof(options.Enabled), value => options.Enabled = value);
        LayerString(section, variables, nameof(options.BaseUrl), shared.BaseUrl, value => options.BaseUrl = value);
        LayerString(section, variables, nameof(options.Path), shared.Path, value => options.Path = value);
        LayerString(section, variables, nameof(options.Model), shared.Model, value => options.Model = value);
        LayerProbability(section, variables, nameof(options.MinProbability), shared.MinProbability, value => options.MinProbability = value);
        LayerInteger(section, variables, nameof(options.MaxAttempts), shared.MaxAttempts, value => options.MaxAttempts = value);
        LayerInteger(section, variables, nameof(options.TimeoutSeconds), shared.TimeoutSeconds, value => options.TimeoutSeconds = value);
        LayerInteger(section, variables, nameof(options.CircuitBreakSeconds), shared.CircuitBreakSeconds, value => options.CircuitBreakSeconds = value);
    }

    private void ApplyLlmMessagesOverrides(LlmMessagesOptions options)
    {
        ApplyRootBooleanOverride(LlmMessagesVariables[nameof(options.Enabled)], nameof(options.Enabled), value => options.Enabled = value);
        ApplyRootStringOverride(LlmMessagesVariables[nameof(options.BaseUrl)], nameof(options.BaseUrl), value => options.BaseUrl = value);
        ApplyRootStringOverride(LlmMessagesVariables[nameof(options.Path)], nameof(options.Path), value => options.Path = value);
        ApplyRootStringOverride(LlmMessagesVariables[nameof(options.Model)], nameof(options.Model), value => options.Model = value);
        ApplyRootIntegerOverride(LlmMessagesVariables[nameof(options.MaxAttempts)], nameof(options.MaxAttempts), value => options.MaxAttempts = value);
        ApplyRootIntegerOverride(LlmMessagesVariables[nameof(options.TimeoutSeconds)], nameof(options.TimeoutSeconds), value => options.TimeoutSeconds = value);
        ApplyRootIntegerOverride(LlmMessagesVariables[nameof(options.CircuitBreakSeconds)], nameof(options.CircuitBreakSeconds), value => options.CircuitBreakSeconds = value);
        ApplyRootIntegerOverride(LlmMessagesVariables[nameof(options.CacheTtlHours)], nameof(options.CacheTtlHours), value => options.CacheTtlHours = value);
        ApplyRootIntegerOverride(LlmMessagesVariables[nameof(options.MinChars)], nameof(options.MinChars), value => options.MinChars = value);
        ApplyRootIntegerOverride(LlmMessagesVariables[nameof(options.BudgetSeconds)], nameof(options.BudgetSeconds), value => options.BudgetSeconds = value);
        ApplyRootStringOverride(LlmMessagesVariables[nameof(options.ApiKey)], nameof(options.ApiKey), value => options.ApiKey = value);
    }

    private bool ApplyRootStringOverride(string variable, string propertyName, Action<string> apply)
    {
        string? value = configuration[variable];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        apply(value);
        LogRootOverrideApplied(variable, propertyName);
        return true;
    }

    private bool ApplyRootIntegerOverride(string variable, string propertyName, Action<int> apply)
    {
        string? value = configuration[variable];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            LogInvalidRootOverride(variable, propertyName, "an invariant whole number");
            return false;
        }

        apply(parsed);
        LogRootOverrideApplied(variable, propertyName);
        return true;
    }

    private bool ApplyRootProbabilityOverride(string variable, string propertyName, Action<double> apply)
    {
        string? value = configuration[variable];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed))
        {
            LogInvalidRootOverride(variable, propertyName, "a finite invariant number");
            return false;
        }

        apply(parsed);
        LogRootOverrideApplied(variable, propertyName);
        return true;
    }

    private void LogInvalidRootOverride(string variable, string propertyName, string expected)
    {
        logger.LogWarning(
            "Ignoring conventional override {EnvVar} for field {Field}: expected {Expected}.",
            variable, propertyName, expected);
    }

    private void LogRootOverrideApplied(string variable, string propertyName)
    {
        logger.LogDebug("Applied conventional override {EnvVar} to root field {Field}.", variable, propertyName);
    }

    /// <summary>
    /// Fills <see cref="ProviderOptions.Secret"/> from the first populated secret suffix in the priority
    /// order for the provider's effective auth scheme (Bearer prefers <c>_AUTH_TOKEN</c>/
    /// <c>_AUTHORIZATION_BEARER</c>; ApiKey prefers <c>_API_KEY</c>), with the off-scheme suffixes as
    /// fallbacks. A blank/whitespace value is treated as absent, so an empty-but-present var neither wins
    /// the slot nor blanks a Secret bound from appsettings.
    /// </summary>
    private void ApplyConventionalSecret(string key, ProviderOptions provider, string prefix)
    {
        string[] order = ResolveEffectiveScheme(provider, prefix) == CredentialAuthScheme.Bearer
            ? BearerSecretSuffixOrder
            : ApiKeySecretSuffixOrder;

        foreach (string suffix in order)
        {
            ConventionalField field = SecretFieldsBySuffix[suffix];
            (string variable, string? value) = ResolveConventionalValue(key, field, prefix);

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            provider.Secret = value;
            LogApplied(variable, key, field.PropertyName);
            return;
        }
    }

    /// <summary>
    /// Resolves the provider's effective auth scheme for secret-suffix prioritization: the conventional
    /// <c>_AUTH_SCHEME</c> env override wins, then the bound <see cref="ProviderOptions.AuthScheme"/>, then
    /// the dialect default (openai → Bearer, anthropic → ApiKey) computed the same way the forwarder does.
    /// Mirrors the <c>_AUTH_SCHEME</c>/<c>_DIALECT</c> env overrides applied later in the field loop so the
    /// secret choice matches the scheme the provider will actually authenticate with.
    /// </summary>
    private CredentialAuthScheme ResolveEffectiveScheme(ProviderOptions provider, string prefix)
    {
        string? schemeValue = configuration[prefix + "_AUTH_SCHEME"];
        if (string.IsNullOrWhiteSpace(schemeValue))
        {
            schemeValue = provider.AuthScheme;
        }

        if (CredentialAuthSchemeParser.TryParse(schemeValue, out CredentialAuthScheme? scheme) && scheme is not null)
        {
            return scheme.Value;
        }

        string? dialectValue = configuration[prefix + "_DIALECT"];
        if (string.IsNullOrWhiteSpace(dialectValue))
        {
            dialectValue = provider.Dialect;
        }

        ApiDialectParser.TryParse(dialectValue, out ApiDialect dialect);
        return UpstreamAuthResolver.DefaultSchemeFor(dialect);
    }

    private (string Variable, string? Value) ResolveConventionalValue(
        string key,
        ConventionalField field,
        string prefix)
    {
        string variable = prefix + field.Suffix;

        // IConfiguration already includes environment variables and matches keys
        // case-insensitively, so OPENCODE_GO_API_KEY (any casing) resolves here.
        string? value = configuration[variable];

        // Treat a blank direct value as absent so an empty-but-present per-provider secret var still
        // falls through to a populated shared base var (the caller also skips blank values entirely).
        if (!string.IsNullOrWhiteSpace(value) || field.PropertyName != nameof(ProviderOptions.Secret))
        {
            return (variable, value);
        }

        string? sharedPrefix = TrySharedProviderSecretPrefix(key);
        if (sharedPrefix is null)
        {
            return (variable, null);
        }

        string sharedVariable = sharedPrefix + field.Suffix;
        return (sharedVariable, configuration[sharedVariable]);
    }

    // The bare base prefix (e.g. "openrouter" -> "OPENROUTER") is always a valid secret
    // fallback for a dialect-suffixed provider ("openrouter-anthropic" -> tries its own
    // "OPENROUTER_ANTHROPIC_API_KEY" first, then falls back to "OPENROUTER_API_KEY"),
    // regardless of whether a sibling or base provider is configured. Previously the fallback
    // was gated on a sibling/base provider existing, which prevented a lone suffixed
    // provider from resolving its secret from the shared base var (LADR-02/03).
    //
    // Uses IndexOf rather than EndsWith so that compound provider keys like
    // "opencode-go-openai-chat" or "opencode-go-openai-responses" resolve from the same
    // base as "opencode-go-openai" — the known dialect suffix is found mid-key and
    // everything from that point onward is stripped.
    private static string? TrySharedProviderSecretPrefix(string key)
    {
        foreach (string suffix in SharedProviderSecretSuffixes)
        {
            int index = key.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                return ToEnvPrefix(key[..index]);
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes a provider key to its conventional env prefix: uppercase, with every run of
    /// non-alphanumeric characters collapsed to a single underscore (<c>opencode-go</c> →
    /// <c>OPENCODE_GO</c>, <c>opencode.anthropic</c> → <c>OPENCODE_ANTHROPIC</c>). The trailing
    /// suffix (e.g. <c>_API_KEY</c>) carries its own leading underscore.
    /// </summary>
    internal static string ToEnvPrefix(string key)
    {
        var builder = new System.Text.StringBuilder(key.Length);
        bool lastWasUnderscore = false;

        foreach (char c in key)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore)
            {
                builder.Append('_');
                lastWasUnderscore = true;
            }
        }

        // A leading/trailing run of separators would leave a stray underscore; trim it so
        // "-opencode-" → "OPENCODE", keeping the prefix clean before the suffix is appended.
        return builder.ToString().Trim('_');
    }

    /// <summary>One conventional suffix, the provider field it targets, and how to apply a string value.</summary>
    internal sealed record ConventionalField(string Suffix, string PropertyName, Action<ProviderOptions, string> Apply);

    /// <summary>
    /// Root-level boolean env override shared by every provider (e.g. <c>IMPOSTER_WHO_MESSAGE_ENABLED</c>).
    /// Mirrors the per-provider bool pattern: blank/whitespace is absent, unparseable logs a Warning and
    /// leaves the bound value untouched, parseable applies via <paramref name="apply"/>.
    /// </summary>
    private bool ApplyRootBooleanOverride(string variable, string propertyName, Action<bool> apply)
    {
        string? value = configuration[variable];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!bool.TryParse(value, out bool parsed))
        {
            logger.LogWarning(
                "Ignoring conventional override {EnvVar} for field {Field}: value '{Value}' is not a recognized boolean.",
                variable, propertyName, value);
            return false;
        }

        apply(parsed);
        LogRootOverrideApplied(variable, propertyName);
        return true;
    }
}
